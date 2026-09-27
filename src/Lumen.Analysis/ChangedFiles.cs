using Lumen.Domain;

namespace Lumen.Analysis;

public static class ChangedFiles
{
    /// <summary>Parses a unified diff into changed files, classifying mechanical ones.</summary>
    /// <param name="mechanicalPaths">The repository's own mechanical path patterns, on top of the built-in rules.</param>
    public static IReadOnlyList<ChangedFile> FromUnifiedDiff(string diff, IReadOnlyList<string>? mechanicalPaths = null) =>
    [
        .. UnifiedDiffParser.Parse(diff)
            .Select(f => new ChangedFile(
                f.Path,
                f.OldPath,
                f.Kind,
                f.Additions,
                f.Deletions,
                f.IsBinary,
                MechanicalClassifier.Classify(f.Path, f.Hunks, mechanicalPaths),
                f.Hunks))
            .OrderBy(f => f.Path, StringComparer.Ordinal),
    ];
}
