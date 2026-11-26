using System.Globalization;
using Lumen.Analysis;
using Lumen.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Lumen.Roslyn.Triage;

/// <summary>
/// "Boilerplate · Not verified" (docs/overhaul.md, Collapsing): new code that matches one of a short list of shapes,
/// folded behind a summary that names what is inside. Only these shapes fold:
/// <list type="bullet">
/// <item>DI registrations: <c>services.Add{Scoped|Transient|Singleton}&lt;…&gt;()</c> with no lambda, not inside an <c>if</c>.</item>
/// <item>DTO and view-model classes of auto-properties with no initialiser and only validation or display attributes.</item>
/// <item>Mapping: plain <c>a.X = b.X</c> blocks, or <c>CreateMap&lt;A,B&gt;()</c> with no <c>ForMember</c>, <c>Ignore</c> or <c>ReverseMap</c>.</item>
/// <item>Test arrange blocks of object construction and assignments, with no mock <c>Setup</c> or <c>Returns</c>.</item>
/// </list>
/// Never folded: anything bindable (a [BindProperty] model, a handler parameter model, an entity), property
/// initialisers, computed getters, non-validation attributes, Razor markup, and edits to existing boilerplate beyond
/// adding or removing members that would themselves fold. Flagged regions are opened again after flags are found.
/// Any doubt means open.
/// </summary>
public sealed class BoilerplateClassifier : IHunkClassifier
{
    /// <summary>Validation and display attributes: the only attributes a folded DTO may carry.</summary>
    public static readonly IReadOnlySet<string> AllowedAttributes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Required", "StringLength", "MaxLength", "MinLength", "Range", "RegularExpression", "EmailAddress", "Phone", "Url",
        "CreditCard", "Compare", "Display", "DisplayName", "DisplayFormat", "DataType", "Description", "Length",
    };

    private static readonly HashSet<string> Lifetimes = new(StringComparer.Ordinal) { "AddScoped", "AddTransient", "AddSingleton" };

    public HunkVerdict? Classify(HunkContext context)
    {
        var file = context.File;
        if (!file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || context.HeadText is not { } head || file.Mechanical.IsMechanical)
        {
            return null;
        }

        var added = context.Hunk.Lines.Where(l => l.Kind == DiffLineKind.Added).ToList();
        var removed = context.Hunk.Lines.Where(l => l.Kind == DiffLineKind.Removed).ToList();
        if (added.Count == 0)
        {
            return null;
        }

        var tree = context.Workspace.GetOrAdd("boilerplate:head:" + file.Path, () => CSharpSyntaxTree.ParseText(head, new CSharpParseOptions(LanguageVersion.Preview)));
        if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return null;
        }

        var root = tree.GetCompilationUnitRoot();
        var lines = tree.GetText().Lines;
        var bindable = Bindable(context);
        var parts = new List<(HunkPart Part, int Start, int End)>();

        // Plain added registrations fold even beside a removal: only the added lines fold, the removal stays open.
        parts.AddRange(Registrations(context, root, lines, added));
        if (removed.Count == 0 || removed.All(r => string.IsNullOrWhiteSpace(r.Text)))
        {
            parts.AddRange(Dtos(context, root, lines, added, bindable));
            parts.AddRange(Mappings(context, root, lines, added));
            if (TestPaths.IsTest(file.Path))
            {
                parts.AddRange(Arranges(context, root, lines, added));
            }
        }

        if (parts.Count == 0 && file.Kind != FileChangeKind.Added && EditedDto(context, root, added, removed, bindable) is { } edit)
        {
            parts.Add(edit);
        }

        // Shapes can overlap (a mapping inside a test's arrange): the first claim on a line keeps it.
        var claimed = new HashSet<DiffLine>();
        parts = [.. parts.Where(p => p.Part.Lines.All(l => !claimed.Contains(l)) && p.Part.Lines.All(claimed.Add))];
        if (parts.Count == 0)
        {
            return null;
        }

        // Folded parts take their lines; everything else in the hunk stays open, as the fallback would have it.
        var covered = parts.SelectMany(p => p.Part.Lines).ToHashSet();
        var rest = context.Hunk.Lines.Where(l => l.Kind != DiffLineKind.Context && !covered.Contains(l)).ToList();
        var fallback = file.Kind == FileChangeKind.Added ? ChangeClass.NewCode : ChangeClass.BehaviourChange;
        var all = parts.Select(p => p.Part).ToList();
        if (rest.Count > 0)
        {
            all.Add(new HunkPart(fallback, FoldKind.Open, [file.Kind == FileChangeKind.Added ? "new file" : "changes existing code"], null, rest));
        }

        return new HunkVerdict(fallback, FoldKind.Open, [.. all.SelectMany(p => p.Reasons).Distinct()]) { Parts = all };
    }

    // ---------------------------------------------------------------- DI registrations

    private static IEnumerable<(HunkPart, int, int)> Registrations(HunkContext context, CompilationUnitSyntax root, TextLineCollection lines, List<DiffLine> added)
    {
        var addedLines = added.Where(l => l.NewNumber is not null).ToDictionary(l => l.NewNumber!.Value);
        var run = new List<(DiffLine Line, string Lifetime, string What)>();
        SyntaxNode? container = null;
        foreach (var statement in root.DescendantNodes().OfType<ExpressionStatementSyntax>().OrderBy(s => s.SpanStart))
        {
            var span = statement.SyntaxTree.GetLineSpan(statement.Span);
            var first = span.StartLinePosition.Line + 1;
            var last = span.EndLinePosition.Line + 1;
            var registration = Registration(statement);
            var allAdded = Enumerable.Range(first, last - first + 1).All(addedLines.ContainsKey);
            var parent = statement.Parent is GlobalStatementSyntax global ? global.Parent : statement.Parent;

            // A run is consecutive sibling statements: anything else between two registrations (a method header, a
            // brace, another statement) ends it, so a fold never hides code that is not a registration.
            if (run.Count > 0 && (parent != container || !OnlyBlankBetween(lines, run[^1].Line.NewNumber!.Value, first)))
            {
                foreach (var part in Flush(run))
                {
                    yield return part;
                }
            }

            if (registration is { } r && allAdded && first == last && !UnderCondition(statement))
            {
                run.Add((addedLines[first], r.Lifetime, r.What));
                container = parent;
                continue;
            }

            foreach (var part in Flush(run))
            {
                yield return part;
            }
        }

        foreach (var part in Flush(run))
        {
            yield return part;
        }

        static IEnumerable<(HunkPart, int, int)> Flush(List<(DiffLine Line, string Lifetime, string What)> run)
        {
            if (run.Count >= 2)
            {
                var counts = run.GroupBy(r => r.Lifetime).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}");
                var summary = string.Create(CultureInfo.InvariantCulture, $"{run.Count} registrations: {string.Join(", ", counts)}");
                yield return (new HunkPart(ChangeClass.NewCode, FoldKind.Boilerplate, [summary], null, [.. run.Select(r => r.Line)]),
                    run[0].Line.NewNumber!.Value, run[^1].Line.NewNumber!.Value);
            }

            run.Clear();
        }
    }

    /// <summary>A plain <c>x.AddScoped&lt;A, B&gt;()</c>: no arguments, so no lambda or factory.</summary>
    public static (string Lifetime, string What)? Registration(ExpressionStatementSyntax statement) =>
        statement.Expression is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: var name } generic },
            ArgumentList.Arguments.Count: 0,
        } && Lifetimes.Contains(name)
            ? (name["Add".Length..].ToLowerInvariant(), string.Join(" → ", generic.TypeArgumentList.Arguments.Select(a => a.ToString())))
            : null;

    private static bool OnlyBlankBetween(TextLineCollection lines, int after, int before) =>
        Enumerable.Range(after + 1, Math.Max(0, before - after - 1)).All(n => n - 1 < lines.Count && string.IsNullOrWhiteSpace(lines[n - 1].ToString()));

    private static bool UnderCondition(SyntaxNode node) =>
        node.Ancestors().Any(a => a is IfStatementSyntax or ElseClauseSyntax or SwitchSectionSyntax or ForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax or ConditionalExpressionSyntax or TryStatementSyntax);

    // ---------------------------------------------------------------- DTOs

    private static IEnumerable<(HunkPart, int, int)> Dtos(HunkContext context, CompilationUnitSyntax root, TextLineCollection lines, List<DiffLine> added, IReadOnlySet<string> bindable)
    {
        var addedLines = added.Where(l => l.NewNumber is not null).ToDictionary(l => l.NewNumber!.Value);
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => t is ClassDeclarationSyntax or RecordDeclarationSyntax))
        {
            var span = type.SyntaxTree.GetLineSpan(type.Span);
            var first = span.StartLinePosition.Line + 1;
            var last = span.EndLinePosition.Line + 1;
            if (!Enumerable.Range(first, last - first + 1).All(addedLines.ContainsKey) || type.Ancestors().OfType<TypeDeclarationSyntax>().Any())
            {
                continue;
            }

            if (DtoSummary(type, context.File.Path, bindable) is not { } summary)
            {
                continue;
            }

            // Include the attribute lines above the declaration that the span already covers.
            yield return (new HunkPart(ChangeClass.NewCode, FoldKind.Boilerplate, [summary], null,
                [.. Enumerable.Range(first, last - first + 1).Select(n => addedLines[n])]), first, last);
        }
    }

    /// <summary>
    /// "OrderEditInput: 11 properties · [Required] ×3 · [StringLength(500)] on Notes"; null when the type isn't a plain
    /// DTO (a method, a field, an initialiser, a computed getter, a non-validation attribute, a nested type), or is
    /// bindable or an entity.
    /// </summary>
    public static string? DtoSummary(TypeDeclarationSyntax type, string path, IReadOnlySet<string> bindable)
    {
        var name = type.Identifier.ValueText;
        if (bindable.Contains(name) || ("/" + path).Contains("/Entities/", StringComparison.OrdinalIgnoreCase)
            || type is RecordDeclarationSyntax { ParameterList.Parameters.Count: > 0 }
            || type.BaseList is not null || type.AttributeLists.Count > 0 || type.TypeParameterList is not null)
        {
            return null;
        }

        var properties = new List<PropertyDeclarationSyntax>();
        foreach (var member in type.Members)
        {
            if (member is not PropertyDeclarationSyntax property || !IsPlainAutoProperty(property))
            {
                return null;
            }

            properties.Add(property);
        }

        if (properties.Count == 0)
        {
            return null;
        }

        var attributes = properties.SelectMany(p => p.AttributeLists.SelectMany(l => l.Attributes).Select(a => (Property: p.Identifier.ValueText, Attribute: a))).ToList();
        if (attributes.Any(a => !AllowedAttributes.Contains(AttributeName(a.Attribute))))
        {
            return null;
        }

        // Every attribute is listed: the bare ones counted, the ones with arguments named with their property.
        var described = attributes
            .GroupBy(a => a.Attribute.ToString(), StringComparer.Ordinal)
            .Select(g => g.Count() > 1 ? $"[{g.Key}] ×{g.Count()}" : $"[{g.Key}] on {g.First().Property}");
        var head = string.Create(CultureInfo.InvariantCulture, $"{name}: {properties.Count} {(properties.Count == 1 ? "property" : "properties")}");
        return attributes.Count == 0 ? head : $"{head} · {string.Join(" · ", described)}";
    }

    /// <summary><c>{ get; set; }</c> or <c>{ get; init; }</c> (optionally <c>required</c>), with no initialiser or body.</summary>
    public static bool IsPlainAutoProperty(PropertyDeclarationSyntax property) =>
        property.Initializer is null
        && property.ExpressionBody is null
        && property.AccessorList is { Accessors: var accessors }
        && accessors.Count is 1 or 2
        && accessors.All(a => a.Body is null && a.ExpressionBody is null && a.Keyword.Kind() is SyntaxKind.GetKeyword or SyntaxKind.SetKeyword or SyntaxKind.InitKeyword)
        && accessors.Any(a => a.Keyword.IsKind(SyntaxKind.GetKeyword))
        && !property.Modifiers.Any(m => m.Kind() is SyntaxKind.StaticKeyword or SyntaxKind.AbstractKeyword or SyntaxKind.VirtualKeyword or SyntaxKind.OverrideKeyword);

    private static string AttributeName(AttributeSyntax attribute)
    {
        var simple = attribute.Name.ToString().Split('.')[^1];
        return simple.EndsWith("Attribute", StringComparison.Ordinal) ? simple[..^"Attribute".Length] : simple;
    }

    /// <summary>
    /// Types that are bound from a request or stored as entities, read from the change's sources: [BindProperty]
    /// property types, handler parameter types in page models, DbSet entity types. These never fold.
    /// </summary>
    public static IReadOnlySet<string> Bindable(HunkContext context) =>
        context.Workspace.GetOrAdd("boilerplate:bindable", () =>
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in context.Sources.Paths.Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var text = context.Sources.Head(path) ?? context.Sources.Base(path);
                if (text is null)
                {
                    continue;
                }

                var root = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview)).GetCompilationUnitRoot();
                foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
                {
                    if (property.AttributeLists.SelectMany(l => l.Attributes).Any(a => AttributeName(a) == "BindProperty")
                        || (property.Type is GenericNameSyntax { Identifier.ValueText: "DbSet" }))
                    {
                        names.UnionWith(TypeNames(property.Type));
                    }
                }

                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText.StartsWith("On", StringComparison.Ordinal)))
                {
                    foreach (var parameter in method.ParameterList.Parameters.Where(p => p.Type is not null))
                    {
                        names.UnionWith(TypeNames(parameter.Type!));
                    }
                }
            }

            return (IReadOnlySet<string>)names;
        });

    private static IEnumerable<string> TypeNames(TypeSyntax type) =>
        type.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Select(n => n.Identifier.ValueText);

    /// <summary>
    /// An existing DTO edited only by adding or removing members that would themselves fold (plain auto-properties with
    /// validation attributes). Anything else in the change keeps it open.
    /// </summary>
    private static (HunkPart, int, int)? EditedDto(HunkContext context, CompilationUnitSyntax root, List<DiffLine> added, List<DiffLine> removed, IReadOnlySet<string> bindable)
    {
        if (context.BaseText is not { } baseText)
        {
            return null;
        }

        var addedNumbers = added.Where(l => l.NewNumber is not null).Select(l => l.NewNumber!.Value).ToList();
        var type = root.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault(t =>
        {
            var span = t.SyntaxTree.GetLineSpan(t.Span);
            return addedNumbers.All(n => n > span.StartLinePosition.Line + 1 && n < span.EndLinePosition.Line + 1);
        });
        if (type is null || DtoSummary(type, context.File.Path, bindable) is not { } summary)
        {
            return null;
        }

        var baseRoot = CSharpSyntaxTree.ParseText(baseText, new CSharpParseOptions(LanguageVersion.Preview)).GetCompilationUnitRoot();
        var baseType = baseRoot.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault(t => t.Identifier.ValueText == type.Identifier.ValueText);
        if (baseType is null || DtoSummary(baseType, context.File.Path, bindable) is null)
        {
            return null;
        }

        // Every changed line must be part of a whole property declaration (or blank).
        bool WholeProperties(IEnumerable<DiffLine> changed, TypeDeclarationSyntax owner, Func<DiffLine, int?> number)
        {
            var spans = owner.Members.OfType<PropertyDeclarationSyntax>().Select(p => p.SyntaxTree.GetLineSpan(p.FullSpan)).Select(s => (s.StartLinePosition.Line + 1, s.EndLinePosition.Line + 1)).ToList();
            return changed.All(l => string.IsNullOrWhiteSpace(l.Text) || (number(l) is { } n && spans.Any(s => n >= s.Item1 && n <= s.Item2)));
        }

        if (!WholeProperties(added, type, l => l.NewNumber) || !WholeProperties(removed, baseType, l => l.OldNumber))
        {
            return null;
        }

        // A property removed and added under the same name was edited (a new type, a new attribute): that stays open.
        static HashSet<string> Named(TypeDeclarationSyntax owner, IEnumerable<int> numbers) =>
        [
            .. owner.Members.OfType<PropertyDeclarationSyntax>()
                .Where(p => p.SyntaxTree.GetLineSpan(p.FullSpan) is var s && numbers.Any(n => n >= s.StartLinePosition.Line + 1 && n <= s.EndLinePosition.Line + 1))
                .Select(p => p.Identifier.ValueText),
        ];

        if (Named(type, addedNumbers).Overlaps(Named(baseType, removed.Where(l => l.OldNumber is not null).Select(l => l.OldNumber!.Value))))
        {
            return null;
        }

        var lines = added.Concat(removed).ToList();
        var verb = removed.Count == 0 ? "added" : added.Count == 0 ? "removed" : "added and removed";
        return (new HunkPart(ChangeClass.BehaviourChange, FoldKind.Boilerplate, [$"{summary} (properties {verb})"], null, lines), addedNumbers.DefaultIfEmpty(0).Min(), addedNumbers.DefaultIfEmpty(0).Max());
    }

    // ---------------------------------------------------------------- mapping

    private static IEnumerable<(HunkPart, int, int)> Mappings(HunkContext context, CompilationUnitSyntax root, TextLineCollection lines, List<DiffLine> added)
    {
        var addedLines = added.Where(l => l.NewNumber is not null).ToDictionary(l => l.NewNumber!.Value);
        int Line(SyntaxNode n) => n.SyntaxTree.GetLineSpan(n.Span).StartLinePosition.Line + 1;
        bool Single(SyntaxNode n) => n.SyntaxTree.GetLineSpan(n.Span) is var s && s.StartLinePosition.Line == s.EndLinePosition.Line;

        // a.X = b.X; statements, consecutive in one block, onto an object the block itself built with `new T()`. Copying
        // onto an existing object (a loaded entity, a parameter) or inside a page or controller is where over-posting
        // lives, so it stays open.
        var bindable = Bindable(context);
        foreach (var block in root.DescendantNodes().OfType<BlockSyntax>().Where(b => !InRequestHandlerType(b)))
        {
            var fresh = FreshObjects(block, bindable);
            var run = new List<(DiffLine Line, string Member)>();
            string? target = null, source = null;
            foreach (var statement in block.Statements.Append(null))
            {
                if (statement is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression, Left: MemberAccessExpressionSyntax left, Right: MemberAccessExpressionSyntax right } }
                    && left.Name.Identifier.ValueText == right.Name.Identifier.ValueText
                    && left.Expression is IdentifierNameSyntax { Identifier.ValueText: var targetName } && fresh.Contains(targetName) && right.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax
                    && Single(statement) && addedLines.TryGetValue(Line(statement), out var line)
                    && (target is null || (left.Expression.ToString() == target && right.Expression.ToString() == source)))
                {
                    target = left.Expression.ToString();
                    source = right.Expression.ToString();
                    run.Add((line, left.Name.Identifier.ValueText));
                    continue;
                }

                if (run.Count >= 3)
                {
                    yield return (new HunkPart(ChangeClass.NewCode, FoldKind.Boilerplate,
                        [string.Create(CultureInfo.InvariantCulture, $"{run.Count} assignments copying {source} → {target}: {string.Join(", ", run.Select(r => r.Member))}")], null,
                        [.. run.Select(r => r.Line)]), run[0].Line.NewNumber!.Value, run[^1].Line.NewNumber!.Value);
                }

                run.Clear();
                target = source = null;
            }
        }

        // CreateMap<A, B>(); with nothing chained.
        var maps = new List<(DiffLine Line, string What)>();
        foreach (var statement in root.DescendantNodes().OfType<ExpressionStatementSyntax>().OrderBy(s => s.SpanStart))
        {
            if (statement.Expression is InvocationExpressionSyntax { Expression: GenericNameSyntax { Identifier.ValueText: "CreateMap" } generic, ArgumentList.Arguments.Count: 0 }
                && Single(statement) && addedLines.TryGetValue(Line(statement), out var line))
            {
                maps.Add((line, string.Join(" → ", generic.TypeArgumentList.Arguments)));
                continue;
            }

            if (maps.Count >= 2)
            {
                yield return (new HunkPart(ChangeClass.NewCode, FoldKind.Boilerplate, [$"{maps.Count} maps: {string.Join(", ", maps.Select(m => m.What))}"], null, [.. maps.Select(m => m.Line)]),
                    maps[0].Line.NewNumber!.Value, maps[^1].Line.NewNumber!.Value);
            }

            maps.Clear();
        }

        if (maps.Count >= 2)
        {
            yield return (new HunkPart(ChangeClass.NewCode, FoldKind.Boilerplate, [$"{maps.Count} maps: {string.Join(", ", maps.Select(m => m.What))}"], null, [.. maps.Select(m => m.Line)]),
                maps[0].Line.NewNumber!.Value, maps[^1].Line.NewNumber!.Value);
        }
    }

    private static readonly HashSet<string> RequestHandlerBases = new(StringComparer.Ordinal) { "PageModel", "Controller", "ControllerBase", "ComponentBase", "Hub" };

    private static bool InRequestHandlerType(SyntaxNode node) =>
        node.Ancestors().OfType<TypeDeclarationSyntax>().Any(t => t.BaseList?.Types.Any(b => b.Type.ToString().Split('.')[^1] is var name && RequestHandlerBases.Contains(name)) == true);

    /// <summary>Locals the block declares as <c>var x = new T()</c> (or <c>T x = new()</c>) where T is neither bound nor an entity.</summary>
    private static HashSet<string> FreshObjects(BlockSyntax block, IReadOnlySet<string> bindable)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in block.Statements.OfType<LocalDeclarationStatementSyntax>())
        {
            foreach (var variable in declaration.Declaration.Variables)
            {
                var type = variable.Initializer?.Value switch
                {
                    ObjectCreationExpressionSyntax o => o.Type,
                    ImplicitObjectCreationExpressionSyntax when !declaration.Declaration.Type.IsVar => declaration.Declaration.Type,
                    _ => null,
                };
                if (type is not null && !TypeNames(type).Any(bindable.Contains))
                {
                    names.Add(variable.Identifier.ValueText);
                }
            }
        }

        return names;
    }

    // ---------------------------------------------------------------- test arrange

    private static IEnumerable<(HunkPart, int, int)> Arranges(HunkContext context, CompilationUnitSyntax root, TextLineCollection lines, List<DiffLine> added)
    {
        var addedLines = added.Where(l => l.NewNumber is not null).ToDictionary(l => l.NewNumber!.Value);
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Body is not null))
        {
            var run = new List<StatementSyntax>();
            foreach (var statement in method.Body!.Statements)
            {
                if (IsArrange(statement))
                {
                    run.Add(statement);
                    continue;
                }

                break;
            }

            if (run.Count < 2)
            {
                continue;
            }

            var span = run[0].SyntaxTree.GetLineSpan(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(run[0].Span.Start, run[^1].Span.End));
            var first = span.StartLinePosition.Line + 1;
            var last = span.EndLinePosition.Line + 1;
            if (last - first < 2 || !Enumerable.Range(first, last - first + 1).All(addedLines.ContainsKey))
            {
                continue;
            }

            var built = run.SelectMany(s => s.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()).Select(o => o.Type.ToString()).Distinct().ToList();
            var summary = string.Create(CultureInfo.InvariantCulture, $"arrange in {method.Identifier.ValueText}: {run.Count} statements building {(built.Count == 0 ? "values" : string.Join(", ", built))}");
            yield return (new HunkPart(ChangeClass.NewCode, FoldKind.Boilerplate, [summary], null, [.. Enumerable.Range(first, last - first + 1).Select(n => addedLines[n])]), first, last);
        }
    }

    /// <summary>Object construction and assignments only: no calls other than constructors, no mock setup.</summary>
    public static bool IsArrange(StatementSyntax statement)
    {
        if (statement is not (LocalDeclarationStatementSyntax or ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax }))
        {
            return false;
        }

        var text = statement.ToString();
        if (text.Contains("Setup", StringComparison.Ordinal) || text.Contains("Returns", StringComparison.Ordinal) || text.Contains("Mock", StringComparison.Ordinal)
            || text.Contains("Substitute", StringComparison.Ordinal) || text.Contains("await", StringComparison.Ordinal))
        {
            return false;
        }

        // A lambda defines behaviour (a fake handler, a callback): that is worth reading, like a mock setup.
        if (statement.DescendantNodes().Any(n => n is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax or ThrowExpressionSyntax))
        {
            return false;
        }

        // Any invocation (a method call) makes it an act, not an arrange; nameof is fine.
        return !statement.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => i.Expression is not IdentifierNameSyntax { Identifier.ValueText: "nameof" });
    }
}
