using Lumen.Analysis;
using Lumen.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lumen.Roslyn.Triage;

/// <summary>Proves a hunk only changes comments or doc comments: tokens and directives are unchanged.</summary>
public sealed class CommentsOnlyClassifier : IHunkClassifier
{
    public HunkVerdict? Classify(HunkContext context)
    {
        if (CSharpHunkProof.For(context) is not { SameTokens: true, SameStructure: true, SameComments: false } proof)
        {
            return null;
        }

        var docs = proof.After.Root.DescendantTrivia().Any(IsDoc) || proof.Before.Root.DescendantTrivia().Any(IsDoc);
        return new(ChangeClass.CommentsOnly, TriageTier.Skip, [docs ? "comments and doc comments only" : "comments only"]);
    }

    private static bool IsDoc(SyntaxTrivia t) =>
        t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);
}

/// <summary>
/// Proves a hunk only adds, removes or reorders plain <c>using Namespace;</c> directives. Aliases, <c>using static</c>
/// and <c>global using</c> are not plain: they change what a name binds to, so a change to one is never imports-only.
/// </summary>
public sealed class ImportsOnlyClassifier : IHunkClassifier
{
    public HunkVerdict? Classify(HunkContext context)
    {
        if (CSharpHunkProof.For(context) is not { SameTokens: false, SameStructure: true, SameComments: true } proof)
        {
            return null;
        }

        var before = Split(proof.Before.Root);
        var after = Split(proof.After.Root);
        if (!CodeFingerprint.SameTokens(before.Code, after.Code))
        {
            return null;
        }

        var added = after.Imports.Except(before.Imports, StringComparer.Ordinal).ToList();
        var removed = before.Imports.Except(after.Imports, StringComparer.Ordinal).ToList();
        var reason = (added.Count, removed.Count) switch
        {
            (0, 0) => "usings reordered",
            (_, 0) => "usings added: " + string.Join(", ", added),
            (0, _) => "usings removed: " + string.Join(", ", removed),
            _ => $"usings changed: +{string.Join(", ", added)} −{string.Join(", ", removed)}",
        };
        return new(ChangeClass.ImportsOnly, TriageTier.Skip, [reason]);
    }

    private static (List<SyntaxToken> Code, List<string> Imports) Split(SyntaxNode root)
    {
        var rest = new List<SyntaxToken>();
        var imports = new List<string>();
        foreach (var token in root.DescendantTokens().Where(t => !t.IsKind(SyntaxKind.EndOfFileToken)))
        {
            if (token.Parent?.FirstAncestorOrSelf<UsingDirectiveSyntax>() is { } directive && IsPlain(directive))
            {
                if (token == directive.SemicolonToken)
                {
                    imports.Add(directive.NamespaceOrType.ToString());
                }

                continue;
            }

            rest.Add(token);
        }

        return (rest, imports);
    }

    private static bool IsPlain(UsingDirectiveSyntax d) =>
        d.Alias is null && d.StaticKeyword.IsKind(SyntaxKind.None) && d.GlobalKeyword.IsKind(SyntaxKind.None) &&
        d.UnsafeKeyword.IsKind(SyntaxKind.None);
}
