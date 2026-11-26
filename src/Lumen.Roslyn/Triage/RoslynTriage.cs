using Lumen.Analysis;

namespace Lumen.Roslyn.Triage;

/// <summary>The full triage pipeline: file-level rules first, then the Roslyn proofs, cheapest and strictest first.</summary>
public static class RoslynTriage
{
    public static IReadOnlyList<IHunkClassifier> Classifiers() =>
    [
        new DestructiveMigrationClassifier(),
        new MechanicalFileClassifier(),
        new FormattingClassifier(),
        new CommentsOnlyClassifier(),
        new ImportsOnlyClassifier(),
        new RenameClassifier(),
        new MoveClassifier(),
        new RippleClassifier(),

        // Not a proof: "Boilerplate · Not verified", only for the listed shapes, and only after every proof declined.
        new BoilerplateClassifier(),
    ];

    public static TriagePipeline CreatePipeline() => new(Classifiers());
}
