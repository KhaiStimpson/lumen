namespace Lumen.Domain;

public enum FileChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
}

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
}

public sealed record DiffLine(DiffLineKind Kind, int? OldNumber, int? NewNumber, string Text);

public sealed record DiffHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    string Header,
    IReadOnlyList<DiffLine> Lines);

public sealed record ChangedFile(
    string Path,
    string? OldPath,
    FileChangeKind Kind,
    int Additions,
    int Deletions,
    bool IsBinary,
    MechanicalClassification Mechanical,
    IReadOnlyList<DiffHunk> Hunks)
{
    /// <summary>Git's similarity index (0–100) for a rename; null for anything else.</summary>
    public int? Similarity { get; init; }

    /// <summary>Head-side line numbers that appear in the diff (added or context); comments may only anchor here.</summary>
    public bool ContainsNewLine(int line) =>
        Hunks.Any(h => h.Lines.Any(l => l.NewNumber == line));

    /// <summary>Base-side line numbers that appear in the diff (removed or context); left-side comments anchor here.</summary>
    public bool ContainsOldLine(int line) =>
        Hunks.Any(h => h.Lines.Any(l => l.OldNumber == line));

    /// <summary>Head-side line numbers that were added or modified by this change.</summary>
    public IEnumerable<int> AddedLines() =>
        Hunks.SelectMany(h => h.Lines).Where(l => l.Kind == DiffLineKind.Added).Select(l => l.NewNumber!.Value);
}

public sealed record MechanicalClassification(bool IsMechanical, string? Reason)
{
    public static readonly MechanicalClassification None = new(false, null);
}
