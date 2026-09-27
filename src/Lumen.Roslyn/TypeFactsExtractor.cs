using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lumen.Roslyn;

/// <summary>
/// Extracts <see cref="TypeFacts"/> from C# syntax. Deliberately syntax-only: it runs on any checkout without
/// a restore or build, works identically on base and head versions of a file, and costs milliseconds per file.
/// </summary>
public static class TypeFactsExtractor
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview, DocumentationMode.None);

    public static SyntaxTree Parse(string path, string text) =>
        CSharpSyntaxTree.ParseText(text, ParseOptions, path);

    public static IReadOnlyList<TypeFacts> Extract(string path, string text, bool isTest) =>
        Extract(Parse(path, text), path, isTest);

    /// <summary>Names of every type declared in the tree (classes, interfaces, records, structs, enums, delegates).</summary>
    public static IEnumerable<string> DeclaredTypeNames(SyntaxTree tree) =>
        tree.GetCompilationUnitRoot().DescendantNodes()
            .Select(n => n switch
            {
                BaseTypeDeclarationSyntax t => t.Identifier.ValueText,
                DelegateDeclarationSyntax d => d.Identifier.ValueText,
                _ => null,
            })
            .OfType<string>();

    public static IReadOnlyList<TypeFacts> Extract(SyntaxTree tree, string path, bool isTest)
    {
        var root = tree.GetCompilationUnitRoot();
        var results = new List<TypeFacts>();

        foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            // Nested types are rarely peers of top-level services; keep the model to top-level classes.
            if (type.Parent is TypeDeclarationSyntax || type.Modifiers.Any(SyntaxKind.StaticKeyword))
            {
                continue;
            }

            results.Add(ExtractType(type, path, isTest));
        }

        return results;
    }

    private static TypeFacts ExtractType(ClassDeclarationSyntax type, string path, bool isTest)
    {
        var traits = new Dictionary<string, TraitOccurrence>(StringComparer.Ordinal);
        var fieldTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        int? constructorLine = null;

        void Add(Trait trait, SyntaxNode node)
        {
            traits.TryAdd(trait.Key, new TraitOccurrence(trait, LineOf(node)));
        }

        foreach (var baseType in type.BaseList?.Types ?? default)
        {
            Add(new Trait(TraitKind.BaseType, Normalize(baseType.Type)), baseType);
        }

        foreach (var attribute in type.AttributeLists.SelectMany(l => l.Attributes))
        {
            Add(new Trait(TraitKind.Attribute, AttributeName(attribute)), attribute);
        }

        if (type.ParameterList is { } primary)
        {
            constructorLine = LineOf(primary);
            foreach (var p in primary.Parameters.Where(p => p.Type is not null))
            {
                Add(new Trait(TraitKind.Dependency, Normalize(p.Type!)), p);
                fieldTypes[p.Identifier.ValueText] = Normalize(p.Type!);
            }
        }

        foreach (var member in type.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    foreach (var v in field.Declaration.Variables)
                    {
                        fieldTypes[v.Identifier.ValueText] = Normalize(field.Declaration.Type);
                    }

                    break;
                case PropertyDeclarationSyntax property:
                    fieldTypes[property.Identifier.ValueText] = Normalize(property.Type);
                    break;
                case ConstructorDeclarationSyntax ctor when !ctor.Modifiers.Any(SyntaxKind.StaticKeyword):
                    constructorLine ??= LineOf(ctor.Identifier);
                    foreach (var p in ctor.ParameterList.Parameters.Where(p => p.Type is not null))
                    {
                        Add(new Trait(TraitKind.Dependency, Normalize(p.Type!)), p);
                    }

                    break;
                case MethodDeclarationSyntax method:
                    foreach (var attribute in method.AttributeLists.SelectMany(l => l.Attributes))
                    {
                        Add(new Trait(TraitKind.Attribute, AttributeName(attribute)), attribute);
                    }

                    break;
            }
        }

        foreach (var node in type.DescendantNodes())
        {
            switch (node)
            {
                case ThrowStatementSyntax { Expression: ObjectCreationExpressionSyntax created }:
                    Add(new Trait(TraitKind.Throws, Normalize(created.Type)), node);
                    break;
                case ThrowExpressionSyntax { Expression: ObjectCreationExpressionSyntax created }:
                    Add(new Trait(TraitKind.Throws, Normalize(created.Type)), node);
                    break;
                case CatchClauseSyntax catchClause when Swallows(catchClause):
                    Add(new Trait(TraitKind.SwallowsExceptions, CaughtType(catchClause)), catchClause);
                    break;
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }:
                    if (ReceiverType(access.Expression, fieldTypes) is { } receiver)
                    {
                        Add(new Trait(TraitKind.Calls, $"{receiver}.{access.Name.Identifier.ValueText}"), node);
                    }

                    break;
            }
        }

        var name = type.Identifier.ValueText;
        return new TypeFacts
        {
            Name = name,
            FullName = $"{NamespaceOf(type)}.{name}".TrimStart('.'),
            Path = path,
            DeclarationLine = LineOf(type.Identifier),
            StartLine = LineOf(type),
            EndLine = type.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
            IsTest = isTest,
            IsAbstract = type.Modifiers.Any(SyntaxKind.AbstractKeyword),
            Suffix = SuffixOf(name),
            Traits = traits,
            ConstructorLine = constructorLine,
        };
    }

    /// <summary>A catch that neither rethrows nor throws something else handles the failure locally.</summary>
    private static bool Swallows(CatchClauseSyntax clause) =>
        !clause.Block.DescendantNodes().Any(n => n is ThrowStatementSyntax or ThrowExpressionSyntax);

    private static string CaughtType(CatchClauseSyntax clause)
    {
        var caught = clause.Declaration?.Type is { } t ? Normalize(t) : "Exception";
        return caught == "Exception" ? "Exception" : "Specific";
    }

    private static string? ReceiverType(ExpressionSyntax receiver, Dictionary<string, string> fieldTypes)
    {
        var identifier = receiver switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var n } => n.Identifier.ValueText,
            _ => null,
        };

        if (identifier is null)
        {
            return null;
        }

        if (fieldTypes.TryGetValue(identifier, out var fieldType))
        {
            return fieldType;
        }

        // A PascalCase receiver that isn't a known member is most likely a static type (e.g. JsonSerializer).
        return char.IsUpper(identifier[0]) ? identifier : null;
    }

    /// <summary>Strips namespaces, nullability and generic arguments so "ILogger&lt;Foo&gt;" and "ILogger&lt;Bar&gt;" match.</summary>
    public static string Normalize(TypeSyntax type) => type switch
    {
        NullableTypeSyntax n => Normalize(n.ElementType),
        QualifiedNameSyntax q => Normalize(q.Right),
        AliasQualifiedNameSyntax a => Normalize(a.Name),
        GenericNameSyntax g => $"{g.Identifier.ValueText}<{new string(',', g.TypeArgumentList.Arguments.Count - 1)}>",
        IdentifierNameSyntax i => i.Identifier.ValueText,
        ArrayTypeSyntax a => Normalize(a.ElementType) + "[]",
        _ => type.ToString(),
    };

    private static string AttributeName(AttributeSyntax attribute)
    {
        var name = Normalize(attribute.Name);
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    private static string NamespaceOf(SyntaxNode node) =>
        string.Join('.', node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));

    private static string? SuffixOf(string name)
    {
        for (var i = name.Length - 1; i > 0; i--)
        {
            if (char.IsUpper(name[i]))
            {
                return i == 0 ? null : name[i..];
            }
        }

        return null;
    }

    private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int LineOf(SyntaxToken token) => token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
