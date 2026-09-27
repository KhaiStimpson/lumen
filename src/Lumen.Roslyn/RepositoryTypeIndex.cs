using Lumen.Analysis;
using Lumen.Domain;

namespace Lumen.Roslyn;

/// <summary>Type facts for every C# class in a checkout, plus the source text needed to show precedent.</summary>
public sealed class RepositoryTypeIndex
{
    private static readonly string[] IgnoredDirectories = ["bin", "obj", ".git", "node_modules", ".vs", "artifacts"];

    private readonly Dictionary<string, string[]> _lines;

    private RepositoryTypeIndex(IReadOnlyList<TypeFacts> types, Dictionary<string, string[]> lines, HashSet<string> declared)
    {
        Types = types;
        _lines = lines;
        DeclaredTypeNames = declared;
    }

    public IReadOnlyList<TypeFacts> Types { get; }

    /// <summary>Every type name declared in the repository; distinguishes repo concepts from framework ones.</summary>
    public IReadOnlySet<string> DeclaredTypeNames { get; }

    /// <summary>Builds an index from in-memory sources keyed by repository-relative path ('/' separators).</summary>
    public static RepositoryTypeIndex FromSources(IEnumerable<KeyValuePair<string, string>> sources)
    {
        var types = new List<TypeFacts>();
        var lines = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (path, text) in sources)
        {
            if (MechanicalClassifier.Classify(path, []).IsMechanical)
            {
                continue;
            }

            lines[path] = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var tree = TypeFactsExtractor.Parse(path, text);
            types.AddRange(TypeFactsExtractor.Extract(tree, path, IsTestPath(path)));
            declared.UnionWith(TypeFactsExtractor.DeclaredTypeNames(tree));
        }

        return new RepositoryTypeIndex(types, lines, declared);
    }

    public static async Task<RepositoryTypeIndex> BuildAsync(string root, CancellationToken cancellationToken)
    {
        var files = EnumerateSourceFiles(root).ToList();
        var sources = new KeyValuePair<string, string>[files.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            cancellationToken,
            async (i, ct) =>
            {
                var text = await File.ReadAllTextAsync(files[i], ct).ConfigureAwait(false);
                sources[i] = new(Path.GetRelativePath(root, files[i]).Replace('\\', '/'), text);
            }).ConfigureAwait(false);

        return FromSources(sources);
    }

    public static bool IsTestPath(string path)
    {
        var segments = path.Split('/');
        return segments.Any(s =>
                   s.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                   s.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                   s.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
                   s.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)) ||
               path.EndsWith("Tests.cs", StringComparison.Ordinal);
    }

    public IEnumerable<TypeFacts> TypesIn(string path) => Types.Where(t => t.Path == path);

    /// <summary>Lines [start..end] (1-based, inclusive) of a file, clamped to the file.</summary>
    public (string Snippet, int StartLine) Snippet(string path, int start, int end)
    {
        if (!_lines.TryGetValue(path, out var lines) || lines.Length == 0)
        {
            return ("", start);
        }

        start = Math.Clamp(start, 1, lines.Length);
        end = Math.Clamp(end, start, lines.Length);
        return (string.Join('\n', Dedent(lines[(start - 1)..end])), start);
    }

    private static IEnumerable<string> Dedent(string[] lines)
    {
        var indent = lines.Where(l => l.Trim().Length > 0)
            .Select(l => l.Length - l.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();
        return lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart());
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (!IgnoredDirectories.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                {
                    pending.Push(sub);
                }
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
            {
                yield return file;
            }
        }
    }
}
