using System.Text;
using Lumen.Domain;

namespace Lumen.Analysis;

/// <summary>Base (merge-base) and head texts of the changed files, keyed by head path.</summary>
public sealed class TriageSources(IReadOnlyDictionary<string, string> baseTexts, IReadOnlyDictionary<string, string> headTexts)
{
    /// <summary>Files larger than this are not loaded; their hunks simply are not proven mechanical.</summary>
    public const int MaxFileChars = 1_000_000;

    public static readonly TriageSources None = new(new Dictionary<string, string>(), new Dictionary<string, string>());

    public string? Base(string path) => baseTexts.TryGetValue(path, out var text) ? text : null;

    public string? Head(string path) => headTexts.TryGetValue(path, out var text) ? text : null;

    public IEnumerable<string> Paths => headTexts.Keys.Union(baseTexts.Keys, StringComparer.Ordinal);

    /// <summary>Reads both sides of every changed, non-binary, non-mechanical file. A file that cannot be read is left out.</summary>
    public static async Task<TriageSources> LoadAsync(PullRequestSnapshot snapshot, IPullRequestCheckout checkout, CancellationToken cancellationToken)
    {
        var baseTexts = new Dictionary<string, string>(StringComparer.Ordinal);
        var headTexts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in snapshot.Files.Where(f => !f.IsBinary && !f.Mechanical.IsMechanical))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (file.Kind != FileChangeKind.Added &&
                    await checkout.ReadBaseFileAsync(file.OldPath ?? file.Path, cancellationToken) is { Length: <= MaxFileChars } b)
                {
                    baseTexts[file.Path] = b;
                }

                if (file.Kind != FileChangeKind.Deleted &&
                    await checkout.ReadHeadFileAsync(file.Path, cancellationToken) is { Length: <= MaxFileChars } h)
                {
                    headTexts[file.Path] = h;
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                // Unreadable sources only cost proofs; the hunk falls back to "not mechanical".
            }
        }

        return new TriageSources(baseTexts, headTexts);
    }
}

/// <summary>Applies a single hunk to the base text, so a proof can compare "base" with "base plus only this hunk".</summary>
public static class HunkApplier
{
    /// <summary>The base text with only <paramref name="hunk"/> applied; null when the hunk's context does not match the base.</summary>
    public static string? ApplyToBase(string baseText, DiffHunk hunk)
    {
        var lines = SplitKeepingEndings(baseText);
        var defaultEnding = lines.Select(l => l.Ending).FirstOrDefault(e => e.Length > 0) ?? "\n";
        var index = hunk.OldCount == 0 ? hunk.OldStart : hunk.OldStart - 1;
        if (index < 0 || index > lines.Count)
        {
            return null;
        }

        var replacement = new List<(string Content, string Ending)>();
        var consumed = 0;
        foreach (var line in hunk.Lines)
        {
            if (line.Kind == DiffLineKind.Added)
            {
                replacement.Add((line.Text.TrimEnd('\r'), defaultEnding));
                continue;
            }

            var at = index + consumed;
            if (at >= lines.Count || !string.Equals(lines[at].Content.TrimEnd('\r'), line.Text.TrimEnd('\r'), StringComparison.Ordinal))
            {
                return null;
            }

            consumed++;
            if (line.Kind == DiffLineKind.Context)
            {
                replacement.Add(lines[at]);
            }
        }

        // An added line that lands at the very end of a file without a final newline must not glue onto what follows.
        var sb = new StringBuilder(baseText.Length + 256);
        foreach (var (content, ending) in lines.Take(index))
        {
            sb.Append(content).Append(ending);
        }

        var tail = lines.Skip(index + consumed).ToList();
        for (var i = 0; i < replacement.Count; i++)
        {
            var (content, ending) = replacement[i];
            sb.Append(content);
            sb.Append(ending.Length == 0 && (i < replacement.Count - 1 || tail.Count > 0) ? defaultEnding : ending);
        }

        foreach (var (content, ending) in tail)
        {
            sb.Append(content).Append(ending);
        }

        return sb.ToString();
    }

    private static List<(string Content, string Ending)> SplitKeepingEndings(string text)
    {
        var lines = new List<(string, string)>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                var hasCr = i > start && text[i - 1] == '\r';
                lines.Add((text[start..(hasCr ? i - 1 : i)], hasCr ? "\r\n" : "\n"));
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add((text[start..], ""));
        }

        return lines;
    }
}
