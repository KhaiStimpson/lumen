using Lumen.Domain;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lumen.Roslyn;

/// <summary>
/// Finds how the C# files of a change use each other, for the Map (plan v3): a file links to another when it names a
/// type declared there. Syntax-only like <see cref="TypeFactsExtractor"/>, so it needs no restore or build; a name
/// declared in more than <see cref="MaxDeclarations"/> files is too ambiguous to link by.
/// </summary>
public static class CodeLinkFinder
{
    private const int MaxDeclarations = 2;

    /// <summary>Links between the given files (repository-relative path → source text), at most one per pair.</summary>
    public static IReadOnlyList<FileLink> Find(IEnumerable<KeyValuePair<string, string>> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var trees = sources.Select(s => (Path: s.Key, Root: TypeFactsExtractor.Parse(s.Key, s.Value).GetCompilationUnitRoot())).ToList();

        var declaredIn = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (path, root) in trees)
        {
            foreach (var name in TypeFactsExtractor.DeclaredTypeNames(root.SyntaxTree).Distinct(StringComparer.Ordinal))
            {
                if (!declaredIn.TryGetValue(name, out var paths))
                {
                    declaredIn[name] = paths = [];
                }

                paths.Add(path);
            }
        }

        var links = new Dictionary<(string From, string To), LinkKind>();
        foreach (var (path, root) in trees)
        {
            var isTest = TestPaths.IsTest(path);

            void Link(string name, LinkKind kind)
            {
                if (!declaredIn.TryGetValue(name, out var targets) || targets.Count > MaxDeclarations)
                {
                    return;
                }

                foreach (var target in targets.Where(t => t != path))
                {
                    // A test file's link to code is what it tests; tests never count as code a file depends on.
                    var actual = isTest && !TestPaths.IsTest(target) ? LinkKind.Tests : kind;
                    if (!links.TryGetValue((path, target), out var existing) || actual > existing)
                    {
                        links[(path, target)] = actual;
                    }
                }
            }

            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                foreach (var baseType in type.BaseList?.Types ?? default)
                {
                    foreach (var name in Names(baseType.Type))
                    {
                        Link(name, LinkKind.Implements);
                    }
                }

                var parameters = (type.ParameterList?.Parameters ?? default)
                    .Concat(type.Members.OfType<ConstructorDeclarationSyntax>().SelectMany(c => c.ParameterList.Parameters));
                foreach (var parameter in parameters.Where(p => p.Type is not null))
                {
                    foreach (var name in Names(parameter.Type!))
                    {
                        Link(name, LinkKind.DependsOn);
                    }
                }
            }

            foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>().Select(n => n.Identifier.ValueText).Distinct(StringComparer.Ordinal))
            {
                Link(name, LinkKind.References);
            }
        }

        return
        [
            .. links
                .OrderBy(l => l.Key.From, StringComparer.Ordinal)
                .ThenBy(l => l.Key.To, StringComparer.Ordinal)
                .Select(l => new FileLink(l.Key.From, l.Key.To, l.Value)),
        ];
    }

    /// <summary>Every simple type name inside a type, e.g. both names in <c>IEnumerable&lt;IEnrichmentProvider&gt;</c>.</summary>
    private static IEnumerable<string> Names(TypeSyntax type) =>
        type.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Select(n => n.Identifier.ValueText);
}
