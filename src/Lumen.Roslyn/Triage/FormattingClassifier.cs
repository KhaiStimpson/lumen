using Lumen.Analysis;
using Lumen.Domain;

namespace Lumen.Roslyn.Triage;

/// <summary>Proves a hunk only moves whitespace and line breaks: tokens, comments and directives are all unchanged.</summary>
public sealed class FormattingClassifier : IHunkClassifier
{
    public HunkVerdict? Classify(HunkContext context) =>
        CSharpHunkProof.For(context) is { SameTokens: true, SameComments: true, SameStructure: true }
            ? new(ChangeClass.Formatting, TriageTier.Skip, ["formatting only"])
            : null;
}
