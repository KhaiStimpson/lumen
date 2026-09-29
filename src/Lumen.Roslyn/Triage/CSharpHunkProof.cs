using Lumen.Analysis;

namespace Lumen.Roslyn.Triage;

/// <summary>
/// The shared first step of every C# proof: the base file and the base file with only this hunk applied, both
/// fingerprinted. Judging one hunk at a time against the whole file means a changed line inside a string or a comment
/// is seen for what it is, which a hunk fragment on its own cannot show.
/// </summary>
public sealed record CSharpHunkProof(CodeFingerprint Before, CodeFingerprint After)
{
    public static bool IsCSharp(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Null when the file is not C#, its base was not loaded, the hunk does not apply, or either side fails to parse.</summary>
    public static CSharpHunkProof? For(HunkContext context) =>
        context.Workspace.GetOrAdd(
            $"proof:{context.File.Path}:{context.Hunk.OldStart}:{context.Hunk.NewStart}",
            () => Create(context));

    private static CSharpHunkProof? Create(HunkContext context)
    {
        if (!IsCSharp(context.File.Path) || context.BaseText is not { } baseText)
        {
            return null;
        }

        var before = context.Workspace.GetOrAdd("fingerprint:base:" + context.File.Path, () => CodeFingerprint.Of(baseText));
        if (before is null || HunkApplier.ApplyToBase(baseText, context.Hunk) is not { } applied)
        {
            return null;
        }

        return CodeFingerprint.Of(applied) is { } after ? new CSharpHunkProof(before, after) : null;
    }

    public bool SameTokens => CodeFingerprint.SameTokens(Before.Tokens, After.Tokens);

    public bool SameComments => Before.Comments.SequenceEqual(After.Comments, StringComparer.Ordinal);

    public bool SameStructure => Before.Structure.SequenceEqual(After.Structure, StringComparer.Ordinal);
}
