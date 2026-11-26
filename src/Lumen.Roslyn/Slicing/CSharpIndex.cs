using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lumen.Roslyn.Slicing;

/// <summary>A member declared in a type: its name and the 1-based head lines it spans.</summary>
public sealed record MemberInfo(string Name, string Kind, int StartLine, int EndLine)
{
    /// <summary>Simple names this member's body and signature refer to (types, members, identifiers).</summary>
    public IReadOnlySet<string> Names { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Attribute names on the member, without the "Attribute" suffix.</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// <summary>Types of parameters marked [FromServices] (handler injection).</summary>
    public IReadOnlyList<TypeRef> FromServices { get; init; } = [];

    /// <summary>Types constructed with <c>new</c> in this member.</summary>
    public IReadOnlySet<string> Constructed { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Member-access names (<c>x.Orders</c> gives "Orders"), for DbSet use.</summary>
    public IReadOnlySet<string> Accessed { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>A type as written in source: its simple name, any qualifier, and the type arguments' names.</summary>
public sealed record TypeRef(string Name, string? Qualifier, IReadOnlyList<TypeRef> Arguments)
{
    public override string ToString() => Arguments.Count == 0 ? Name : $"{Name}<{string.Join(", ", Arguments)}>";
}

/// <summary>A type declared in the repository.</summary>
public sealed record TypeInfo(string Name, string Namespace, string Path, string Kind, int StartLine, int EndLine)
{
    public string QualifiedName => Namespace.Length == 0 ? Name : $"{Namespace}.{Name}";

    /// <summary>The name without any enclosing types ("InputModel" for "EditModel.InputModel").</summary>
    public string SimpleName => Name[(Name.LastIndexOf('.') + 1)..];

    public IReadOnlyList<TypeRef> BaseTypes { get; init; } = [];

    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// <summary>Raw attribute text, e.g. <c>HtmlTargetElement("currency-input")</c>.</summary>
    public IReadOnlyList<string> AttributeText { get; init; } = [];

    /// <summary>Constructor and primary-constructor parameter types: what the type has injected.</summary>
    public IReadOnlyList<TypeRef> Injected { get; init; } = [];

    public IReadOnlyList<MemberInfo> Members { get; init; } = [];

    /// <summary>DbSet&lt;T&gt; properties: property name → entity type name.</summary>
    public IReadOnlyDictionary<string, string> DbSets { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Every simple name used anywhere in the type (for "references").</summary>
    public IReadOnlySet<string> Names { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string> Constructed { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string> Accessed { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public bool IsInterface => Kind == "interface";

    public bool DerivesFrom(string simpleName) => BaseTypes.Any(b => b.Name == simpleName);
}

/// <summary>One C# file: namespace, usings, its types, string literals (script paths in tag helpers) and DI registrations.</summary>
public sealed record CSharpFile(string Path, string Namespace, IReadOnlyList<string> Usings, IReadOnlyList<TypeInfo> Types)
{
    public bool HasGlobalUsings { get; init; }

    public IReadOnlyList<string> GlobalUsings { get; init; } = [];

    /// <summary>String literals that look like script or page paths ("/js/x.js", "?handler=X").</summary>
    public IReadOnlyList<string> PathLiterals { get; init; } = [];

    /// <summary>Simple names used in top-level statements (Program.cs).</summary>
    public IReadOnlySet<string> TopLevelNames { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary><c>services.AddX&lt;TService, TImpl&gt;()</c>: service → implementation simple names.</summary>
    public IReadOnlyList<(string Service, string Implementation)> Registrations { get; init; } = [];

    /// <summary>Minimal-API routes: <c>app.MapGet("/x", …)</c> patterns with their line.</summary>
    public IReadOnlyList<(string Route, int Line)> MinimalApis { get; init; } = [];
}

/// <summary>Syntax-only C# reading: no build or restore needed, so it works on any checkout.</summary>
public static class CSharpReader
{
    private static readonly CSharpParseOptions Options = new(LanguageVersion.Preview, DocumentationMode.None);

    public static CSharpFile Read(string path, string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text, Options, path);
        var root = tree.GetCompilationUnitRoot();
        var fileNamespace = root.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString()
            ?? root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString()
            ?? "";
        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(u => u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name is not null)
            .Select(u => u.Name!.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var globals = root.Usings.Where(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword) && u.Name is not null).Select(u => u.Name!.ToString()).ToList();

        var types = new List<TypeInfo>();
        foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            types.Add(ReadType(tree, type, NamespaceOf(type) ?? fileNamespace));
        }

        foreach (var del in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
        {
            var span = tree.GetLineSpan(del.Span);
            types.Add(new TypeInfo(del.Identifier.ValueText, NamespaceOf(del) ?? fileNamespace, path, "delegate", span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1));
        }

        var literals = root.DescendantTokens()
            .Where(t => t.IsKind(SyntaxKind.StringLiteralToken) || t.IsKind(SyntaxKind.InterpolatedStringTextToken))
            .Select(t => t.ValueText)
            .Where(LooksLikePath)
            .ToList();

        var topLevel = root.Members.OfType<GlobalStatementSyntax>().ToList();
        var topNames = topLevel.SelectMany(g => g.DescendantNodes().OfType<SimpleNameSyntax>()).Select(n => n.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);

        return new CSharpFile(path, fileNamespace, usings, types)
        {
            HasGlobalUsings = globals.Count > 0,
            GlobalUsings = globals,
            PathLiterals = literals,
            TopLevelNames = topNames,
            Registrations = Registrations(root),
            MinimalApis = MinimalApis(tree, root),
        };
    }

    private static bool LooksLikePath(string value) =>
        value.Length is > 3 and < 300
        && (value.Contains("<script", StringComparison.OrdinalIgnoreCase)
            || (!value.Contains(' ', StringComparison.Ordinal)
                && (value.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || value.Contains("handler=", StringComparison.OrdinalIgnoreCase))));

    private static string? NamespaceOf(SyntaxNode node)
    {
        var parts = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).Reverse().ToList();
        return parts.Count == 0 ? null : string.Join('.', parts);
    }

    private static TypeInfo ReadType(SyntaxTree tree, BaseTypeDeclarationSyntax type, string ns)
    {
        var span = tree.GetLineSpan(type.Span);
        var kind = type switch
        {
            InterfaceDeclarationSyntax => "interface",
            EnumDeclarationSyntax => "enum",
            RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "struct" : "record",
            StructDeclarationSyntax => "struct",
            _ => "class",
        };
        var outer = type.Ancestors().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.ValueText).Reverse().ToList();
        var name = type.Identifier.ValueText;
        var bases = type.BaseList?.Types.Select(b => Ref(b.Type)).ToList() ?? [];
        var attributes = type.AttributeLists.SelectMany(l => l.Attributes).ToList();

        var injected = new List<TypeRef>();
        var members = new List<MemberInfo>();
        var dbSets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (type is TypeDeclarationSyntax typed)
        {
            // A class's primary constructor is how it is injected; a record's is its data, not a dependency.
            if (typed is not RecordDeclarationSyntax)
            {
                injected.AddRange((typed.ParameterList?.Parameters ?? default).Where(p => p.Type is not null).Select(p => Ref(p.Type!)));
            }
            foreach (var member in typed.Members)
            {
                if (member is BaseTypeDeclarationSyntax)
                {
                    continue;
                }

                if (member is ConstructorDeclarationSyntax ctor)
                {
                    injected.AddRange(ctor.ParameterList.Parameters.Where(p => p.Type is not null).Select(p => Ref(p.Type!)));
                }

                if (member is PropertyDeclarationSyntax { Type: GenericNameSyntax { Identifier.ValueText: "DbSet", TypeArgumentList.Arguments: [var entity] } } set)
                {
                    dbSets[set.Identifier.ValueText] = Ref(entity).Name;
                }

                members.Add(ReadMember(tree, member));
            }
        }

        var allNames = type.DescendantNodes().OfType<SimpleNameSyntax>().Select(n => n.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        allNames.Remove(name);
        return new TypeInfo(outer.Count == 0 ? name : string.Join('.', outer) + "." + name, ns, tree.FilePath, kind, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1)
        {
            BaseTypes = bases,
            Attributes = [.. attributes.Select(a => TrimAttribute(a.Name.ToString()))],
            AttributeText = [.. attributes.Select(a => a.ToString())],
            Injected = injected,
            Members = members,
            DbSets = dbSets,
            Names = allNames,
            Constructed = Constructed(type),
            Accessed = Accessed(type),
        };
    }

    private static MemberInfo ReadMember(SyntaxTree tree, MemberDeclarationSyntax member)
    {
        var span = tree.GetLineSpan(member.Span);
        var name = member switch
        {
            MethodDeclarationSyntax m => m.Identifier.ValueText,
            PropertyDeclarationSyntax p => p.Identifier.ValueText,
            ConstructorDeclarationSyntax => ".ctor",
            FieldDeclarationSyntax f => string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.ValueText)),
            EventDeclarationSyntax e => e.Identifier.ValueText,
            EventFieldDeclarationSyntax e => string.Join(",", e.Declaration.Variables.Select(v => v.Identifier.ValueText)),
            IndexerDeclarationSyntax => "this[]",
            OperatorDeclarationSyntax o => "op_" + o.OperatorToken.ValueText,
            EnumMemberDeclarationSyntax em => em.Identifier.ValueText,
            _ => member.Kind().ToString(),
        };
        var kind = member switch
        {
            MethodDeclarationSyntax => "method",
            PropertyDeclarationSyntax => "property",
            ConstructorDeclarationSyntax => "constructor",
            FieldDeclarationSyntax => "field",
            _ => "member",
        };
        var fromServices = member is MethodDeclarationSyntax method
            ? method.ParameterList.Parameters
                .Where(p => p.Type is not null && p.AttributeLists.SelectMany(l => l.Attributes).Any(a => TrimAttribute(a.Name.ToString()) == "FromServices"))
                .Select(p => Ref(p.Type!))
                .ToList()
            : [];
        return new MemberInfo(name, kind, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1)
        {
            Names = member.DescendantNodes().OfType<SimpleNameSyntax>().Select(n => n.Identifier.ValueText).ToHashSet(StringComparer.Ordinal),
            Attributes = [.. member.AttributeLists.SelectMany(l => l.Attributes).Select(a => TrimAttribute(a.Name.ToString()))],
            FromServices = fromServices,
            Constructed = Constructed(member),
            Accessed = Accessed(member),
        };
    }

    /// <summary>Types constructed with <c>new T(...)</c>, and target-typed <c>new(...)</c> where the target type is written.</summary>
    private static HashSet<string> Constructed(SyntaxNode node)
    {
        var names = node.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Select(o => Ref(o.Type).Name).ToHashSet(StringComparer.Ordinal);
        foreach (var implicitNew in node.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>())
        {
            var target = implicitNew.Parent switch
            {
                ArrowExpressionClauseSyntax { Parent: MethodDeclarationSyntax m } => m.ReturnType,
                ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax p } => p.Type,
                EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax v } } => v.Type,
                EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax p } => p.Type,
                ReturnStatementSyntax r => r.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.ReturnType,
                _ => null,
            };
            if (target is not null && target is not IdentifierNameSyntax { IsVar: true })
            {
                names.Add(Ref(target).Name);
            }
        }

        return names;
    }

    private static HashSet<string> Accessed(SyntaxNode node) =>
        node.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Select(m => m.Name.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);

    public static TypeRef Ref(TypeSyntax type) => type switch
    {
        NullableTypeSyntax n => Ref(n.ElementType),
        ArrayTypeSyntax a => Ref(a.ElementType),
        QualifiedNameSyntax q => Ref(q.Right) with { Qualifier = q.Left.ToString() },
        AliasQualifiedNameSyntax a => Ref(a.Name),
        GenericNameSyntax g => new TypeRef(g.Identifier.ValueText, null, [.. g.TypeArgumentList.Arguments.Select(Ref)]),
        SimpleNameSyntax s => new TypeRef(s.Identifier.ValueText, null, []),
        PredefinedTypeSyntax p => new TypeRef(p.Keyword.ValueText, null, []),
        _ => new TypeRef(type.ToString(), null, []),
    };

    private static string TrimAttribute(string name)
    {
        var simple = name.Split('.')[^1];
        return simple.EndsWith("Attribute", StringComparison.Ordinal) ? simple[..^"Attribute".Length] : simple;
    }

    /// <summary><c>AddScoped&lt;IOrderService, OrderService&gt;()</c> and <c>AddScoped&lt;OrderService&gt;()</c>.</summary>
    private static List<(string, string)> Registrations(CompilationUnitSyntax root)
    {
        var found = new List<(string, string)>();
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax g }
                && g.Identifier.ValueText is "AddScoped" or "AddTransient" or "AddSingleton" or "TryAddScoped" or "TryAddTransient" or "TryAddSingleton")
            {
                var args = g.TypeArgumentList.Arguments.Select(a => Ref(a).Name).ToList();
                if (args.Count == 2)
                {
                    found.Add((args[0], args[1]));
                }
                else if (args.Count == 1)
                {
                    found.Add((args[0], args[0]));
                }
            }
        }

        return found;
    }

    private static List<(string, int)> MinimalApis(SyntaxTree tree, CompilationUnitSyntax root) =>
    [
        .. root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "MapGet" or "MapPost" or "MapPut" or "MapDelete" or "MapPatch" or "MapMethods" })
            .Select(c => (c.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax lit ? lit.Token.ValueText : "?",
                tree.GetLineSpan(c.Span).StartLinePosition.Line + 1)),
    ];
}
