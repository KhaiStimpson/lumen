using Lumen.Analysis;

namespace Lumen.Roslyn.Triage;

/// <summary>The full triage pipeline: file-level rules first, then the Roslyn proofs, cheapest and strictest first.</summary>
public static class RoslynTriage
{
    public static IReadOnlyList<IHunkClassifier> Classifiers() =>
    [
        new MechanicalFileClassifier(),
        new FormattingClassifier(),
        new CommentsOnlyClassifier(),
        new ImportsOnlyClassifier(),
    ];

    public static TriagePipeline CreatePipeline() => new(Classifiers());
}
