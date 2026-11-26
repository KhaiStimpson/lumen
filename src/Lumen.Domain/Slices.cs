using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Lumen.Domain;

/// <summary>What a slice is (docs/overhaul.md, Feature slices).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SliceKind>))]
public enum SliceKind
{
    /// <summary>The code behind one entry point.</summary>
    Feature,

    /// <summary>A file reached from three or more slices, reviewed once.</summary>
    Shared,

    /// <summary>Changed code no seed reaches, used by unchanged code.</summary>
    UnlinkedUsed,

    /// <summary>Changed code nothing uses.</summary>
    UnlinkedUnused,

    /// <summary>Files whose every change folds as proven mechanical.</summary>
    MechanicalOnly,
}

/// <summary>The kind of entry point a feature slice grows from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SeedKind>))]
public enum SeedKind
{
    RazorPage,
    WebFormsPage,
    ApiController,
    MinimalApi,
    HostedService,
    Middleware,
    Filter,
    TestOnly,
}

/// <summary>
/// A link between two nodes, always with its reason in words. A node is a repository-relative path, or
/// "path::Type.Member" for a member. A possible link is shown and never moves code into a slice.
/// </summary>
public sealed record SliceEdge(string From, string To, string Reason, bool Possible = false);

/// <summary>A changed file in a slice: its place in the reading order, why it is there, and its slice members.</summary>
public sealed record SliceFile(string Path, int Order, string Why)
{
    /// <summary>Changed members ("Type.Member") that belong to this slice; empty when the whole file does.</summary>
    public IReadOnlyList<string> Members { get; init; } = [];

    /// <summary>The handler sub-section ("OnGet", "OnPostSave") in a split slice; empty otherwise.</summary>
    public string Section { get; init; } = "";

    /// <summary>For Unlinked: how many unchanged files use this one.</summary>
    public int OutsideCallers { get; init; }
}

public sealed record Slice(string Id, string Title, SliceKind Kind, string SeedPath, IReadOnlyList<SliceFile> Files)
{
    public SeedKind? Seed { get; init; }

    public IReadOnlyList<string> Sections { get; init; } = [];

    public IReadOnlyList<SliceEdge> PossibleLinks { get; init; } = [];

    public IReadOnlyList<SliceEdge> PageLinks { get; init; } = [];

    /// <summary>A slice's id is derived from what it is (its kind and seed), so marks and URLs survive a re-analysis.</summary>
    public static string IdFor(SliceKind kind, string seed) =>
        "s" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{seed}")))[..12].ToLowerInvariant();
}

/// <summary>Every slice for one head, the membership edges, and whether slicing fell back to the file list.</summary>
public sealed record SliceResult(IReadOnlyList<Slice> Slices, IReadOnlyList<SliceEdge> Edges)
{
    public bool FellBack { get; init; }

    public string? FallbackReason { get; init; }

    public TimeSpan Duration { get; init; }

    public static SliceResult Fallback(string reason, TimeSpan duration) => new([], []) { FellBack = true, FallbackReason = reason, Duration = duration };
}

/// <summary>
/// The fingerprint of one unit's normalised changed lines (a file, or "path::Type.Member" in a large file): what a
/// reviewed mark records. Line numbers and surrounding context are not part of it, so a push elsewhere leaves it alone.
/// </summary>
public sealed record UnitFingerprint(string Unit, string Path, string Fingerprint, int OpenLines);

/// <summary>A red flag's kind: one per row of the overhaul's red-flag table.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FlagKind>))]
public enum FlagKind
{
    WeakenedTests,
    AntiforgeryOff,
    AuthorisationLoosened,
    OverPosting,
    GuardRemoved,
    RawOutput,
    RawSql,
    QuerySafety,
    SwallowedErrors,
    WarningsSilenced,
    DependenciesAndBuild,
    SecurityConfig,
}

/// <summary>
/// A deterministic fact about one line: no tier, no score, never reorders anything. <see cref="Message"/> says only
/// what the detector found. Identity is (kind, member, normalised line), so it survives pushes that move the line.
/// </summary>
public sealed record RedFlag(FlagKind Kind, string Path, DiffSide Side, int Line, string Message)
{
    /// <summary>The enclosing member ("Type.Member"), or empty at file level.</summary>
    public string Member { get; init; } = "";

    /// <summary>The flagged line with whitespace collapsed; part of the identity.</summary>
    public string NormalisedLine { get; init; } = "";

    public string Id => "f" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Kind}|{Path}|{Member}|{NormalisedLine}")))[..12].ToLowerInvariant();

    public static string Normalise(string line) => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
