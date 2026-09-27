using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Lumen.Analysis;

public enum SensitivityPreset
{
    Quiet,
    Balanced,
    Thorough,
    Custom,
}

/// <summary>
/// How picky convention detection is. One set of numbers drives both the detector and the rules, so they can't
/// disagree: the detector's looser pre-filter is derived from <see cref="MinimumSupport"/> rather than set.
/// </summary>
public sealed record ReviewSensitivity
{
    public static readonly ReviewSensitivity Quiet = new() { MinimumPeers = 5, MinimumSupport = 0.85, MinimumLift = 2.0, MaxPointsPerType = 1, MaxExamples = 3 };

    public static readonly ReviewSensitivity Balanced = new();

    public static readonly ReviewSensitivity Thorough = new() { MinimumPeers = 3, MinimumSupport = 0.65, MinimumLift = 1.2, MaxPointsPerType = 3, MaxExamples = 6 };

    /// <summary>Classes sharing a role before a pattern among them counts.</summary>
    public int MinimumPeers { get; init; } = 3;

    /// <summary>Share of peers (0..1) that must follow a pattern before its absence is surfaced.</summary>
    public double MinimumSupport { get; init; } = 0.75;

    /// <summary>How much likelier the trait must be in the role than across the repository.</summary>
    public double MinimumLift { get; init; } = 1.5;

    /// <summary>Most deviations surfaced for one changed class.</summary>
    public int MaxPointsPerType { get; init; } = 2;

    /// <summary>Peer classes listed as precedent on a card.</summary>
    public int MaxExamples { get; init; } = 4;

    /// <summary>Candidates below this never reach the policy. Kept 15 points under the decision threshold.</summary>
    [JsonIgnore]
    public double CandidateSupport => Math.Max(0.5, MinimumSupport - 0.15);

    [JsonIgnore]
    public SensitivityPreset Preset =>
        this == Quiet ? SensitivityPreset.Quiet :
        this == Balanced ? SensitivityPreset.Balanced :
        this == Thorough ? SensitivityPreset.Thorough :
        SensitivityPreset.Custom;

    public static ReviewSensitivity For(SensitivityPreset preset) => preset switch
    {
        SensitivityPreset.Quiet => Quiet,
        SensitivityPreset.Thorough => Thorough,
        _ => Balanced,
    };

    /// <summary>Pulls hand-edited values back into the range the UI offers.</summary>
    public ReviewSensitivity Clamped() => new()
    {
        MinimumPeers = Math.Clamp(MinimumPeers, 2, 20),
        MinimumSupport = Math.Round(Math.Clamp(MinimumSupport, 0.5, 1.0), 2),
        MinimumLift = Math.Round(Math.Clamp(MinimumLift, 1.0, 5.0), 1),
        MaxPointsPerType = Math.Clamp(MaxPointsPerType, 1, 10),
        MaxExamples = Math.Clamp(MaxExamples, 1, 10),
    };
}

/// <summary>
/// The review settings in force for one repository: the global defaults with that repository's overrides applied.
/// Resolved once when an analysis starts, so a run never sees a half-applied change.
/// </summary>
public sealed record ReviewSettings
{
    public static readonly ReviewSettings Default = new();

    public ReviewSensitivity Sensitivity { get; init; } = ReviewSensitivity.Balanced;

    /// <summary>Type or member names never treated as a convention, on top of the detector's built-in list.</summary>
    public IReadOnlyList<string> IgnoredNames { get; init; } = [];

    /// <summary>Path patterns treated as mechanical (collapsed, never analysed), on top of the built-in rules.</summary>
    public IReadOnlyList<string> MechanicalPaths { get; init; } = [];

    /// <summary>Path patterns whose changes are never flagged. They still count as precedent for other code.</summary>
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
