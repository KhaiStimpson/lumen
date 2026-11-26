using System.Text.RegularExpressions;

namespace Lumen.Analysis;

/// <summary>
/// The review settings in force for one repository: the global defaults with that repository's overrides applied.
/// Resolved once when an analysis starts, so a run never sees a half-applied change.
/// </summary>
public sealed record ReviewSettings
{
    public static readonly ReviewSettings Default = new();

    /// <summary>Path patterns treated as mechanical (collapsed, never analysed), on top of the built-in rules.</summary>
    public IReadOnlyList<string> MechanicalPaths { get; init; } = [];

    /// <summary>Path patterns whose changes never raise a red flag.</summary>
    public IReadOnlyList<string> SkippedPaths { get; init; } = [];

    public bool IsSkipped(string path) => PathPattern.MatchesAny(SkippedPaths, path);
}

/// <summary>
/// Gitignore-style path patterns: <c>*</c> within a segment, <c>**</c> across segments, <c>?</c> one character.
/// A pattern without a <c>/</c> matches the file name in any folder; a trailing <c>/</c> matches everything under
/// that folder.
/// </summary>
public static class PathPattern
{
    private static readonly Dictionary<string, Regex> Cache = new(StringComparer.Ordinal);
    private static readonly Lock CacheGate = new();

    public static bool MatchesAny(IEnumerable<string> patterns, string path) => patterns.Any(p => Matches(p, path));

    public static bool Matches(string pattern, string path)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = pattern.Trim().Replace('\\', '/');
        return trimmed.Length > 0 && RegexFor(trimmed).IsMatch(path.Replace('\\', '/'));
    }

    private static Regex RegexFor(string pattern)
    {
        lock (CacheGate)
        {
            if (!Cache.TryGetValue(pattern, out var regex))
            {
                regex = new Regex(Translate(pattern), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                Cache[pattern] = regex;
            }

            return regex;
        }
    }

    private static string Translate(string pattern)
    {
        var anchored = pattern.Contains('/', StringComparison.Ordinal) && !pattern.StartsWith("**/", StringComparison.Ordinal);
        var body = pattern.TrimStart('/');
        if (body.EndsWith('/'))
        {
            body += "**";
        }

        var regex = new System.Text.StringBuilder(anchored ? "^" : "(^|/)");
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '*' && i + 1 < body.Length && body[i + 1] == '*')
            {
                var slash = i + 2 < body.Length && body[i + 2] == '/';
                regex.Append(slash ? "(.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else
            {
                regex.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString()),
                });
            }
        }

        return regex.Append('$').ToString();
    }
}
