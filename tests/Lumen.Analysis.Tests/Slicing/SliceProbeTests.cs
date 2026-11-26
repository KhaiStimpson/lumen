using Lumen.Roslyn.Slicing;
using Lumen.TestSupport;
using Xunit.Abstractions;

namespace Lumen.Analysis.Tests.Slicing;

/// <summary>Opt-in (LUMEN_SLICE_PROBE=1): prints the fixture's link graph, for reading by hand.</summary>
public sealed class SliceProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void PrintsTheFixtureLinkGraph()
    {
        if (Environment.GetEnvironmentVariable("LUMEN_SLICE_PROBE") != "1")
        {
            return;
        }

        var graph = LinkResolver.Build(FixtureSources.Index("head"));
        foreach (var link in graph.All.OrderBy(l => l.From, StringComparer.Ordinal).ThenBy(l => l.Kind))
        {
            output.WriteLine($"{link.Kind,-13} {link.From} -> {link.To}   {link.Reason}");
        }
    }
}

/// <summary>Reads the Razor Pages fixture's base or head tree from disk as repository-relative sources.</summary>
internal static class FixtureSources
{
    public static IEnumerable<KeyValuePair<string, string>> Read(string tree)
    {
        var root = Path.Combine(FixtureRepository.FixtureDirectory, tree);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (RepositoryIndex.IsIndexed(relative))
            {
                yield return new(relative, File.ReadAllText(file));
            }
        }
    }

    public static RepositoryIndex Index(string tree) => RepositoryIndex.Build(Read(tree));
}
