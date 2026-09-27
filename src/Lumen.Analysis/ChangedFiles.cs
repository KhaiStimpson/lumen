using Lumen.Domain;

namespace Lumen.Analysis;

public static class ChangedFiles
{
    /// <summary>Parses a unified diff into changed files, classifying mechanical ones.</summary>
    public static IReadOnlyList<ChangedFile> FromUnifiedDiff(string diff) =>
    [
        .. UnifiedDiffParser.Parse(diff)
            .Select(f => new ChangedFile(
                f.Path,
                f.OldPath,
                f.Kind,
                f.Additions,
                f.Deletions,
                f.IsBinary,
                MechanicalClassifier.Classify(f.Path, f.Hunks),
                f.Hunks))
            .OrderBy(f => f.Path, StringComparer.Ordinal),
    ];
}
