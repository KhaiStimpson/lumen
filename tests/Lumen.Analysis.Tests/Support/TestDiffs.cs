using System.Text;
using Lumen.Domain;

namespace Lumen.Analysis.Tests.Support;

/// <summary>Builds hand-written unified diffs and pull request snapshots for detector tests.</summary>
internal static class TestDiffs
{
    public static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    /// <summary>A diff that adds <paramref name="path"/> with the given content.</summary>
    public static string AddedFile(string path, string text)
    {
        var lines = Lines(text);
        var sb = new StringBuilder();
        sb.Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n');
        sb.Append("new file mode 100644\n");
        sb.Append("index 0000000..1111111\n");
        sb.Append("--- /dev/null\n");
        sb.Append("+++ b/").Append(path).Append('\n');
        sb.Append("@@ -0,0 +1,").Append(lines.Length).Append(" @@\n");
        foreach (var line in lines)
        {
            sb.Append('+').Append(line).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>A diff that rewrites <paramref name="path"/> wholesale: every base line removed, every head line added.</summary>
    public static string RewrittenFile(string path, string baseText, string headText)
    {
        var before = Lines(baseText);
        var after = Lines(headText);
        var sb = new StringBuilder();
        sb.Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n');
        sb.Append("index 1111111..2222222 100644\n");
        sb.Append("--- a/").Append(path).Append('\n');
        sb.Append("+++ b/").Append(path).Append('\n');
        sb.Append("@@ -1,").Append(before.Length).Append(" +1,").Append(after.Length).Append(" @@\n");
        foreach (var line in before)
        {
            sb.Append('-').Append(line).Append('\n');
        }

        foreach (var line in after)
        {
            sb.Append('+').Append(line).Append('\n');
        }

        return sb.ToString();
    }

    public static PullRequestSnapshot Snapshot(string diff) => Snapshot(Analysis.ChangedFiles.FromUnifiedDiff(diff));

    public static PullRequestSnapshot Snapshot(IReadOnlyList<ChangedFile> files) =>
        new(
            new PullRequestKey(new RepositoryRef("acme", "shop"), 42),
            "base-sha",
            "head-sha",
            "merge-base-sha",
            new PullRequestMetadata("Test PR", "someone", "open", false, "main", "feature", "https://github.com/acme/shop/pull/42", null, DateTimeOffset.UnixEpoch),
            files,
            []);

    public static Func<string, CancellationToken, Task<string?>> BaseReader(IReadOnlyDictionary<string, string> baseSources) =>
        (path, _) => Task.FromResult(baseSources.TryGetValue(path, out var text) ? text : null);

    /// <summary>1-based line of the first line containing <paramref name="marker"/>.</summary>
    public static int LineOf(string text, string marker)
    {
        var lines = Lines(text);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(marker, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        throw new ArgumentException($"Marker '{marker}' not found.", nameof(marker));
    }
}
