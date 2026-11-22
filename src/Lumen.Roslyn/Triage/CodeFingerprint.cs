using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lumen.Roslyn.Triage;

/// <summary>
/// A C# file reduced to what the compiler sees (tokens), what a reader sees (comments) and what switches code on and
/// off (directives, disabled text, skipped tokens). Two files with equal <see cref="Tokens"/> and <see cref="Structure"/>
/// compile to the same program; whitespace and line breaks are not part of any of the three.
/// </summary>
public sealed partial class CodeFingerprint
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview, DocumentationMode.Parse);

    private CodeFingerprint(SyntaxNode root, IReadOnlyList<SyntaxToken> tokens, IReadOnlyList<string> comments, IReadOnlyList<string> structure)
    {
        Root = root;
        Tokens = tokens;
        Comments = comments;
        Structure = structure;
    }

    public SyntaxNode Root { get; }

    public IReadOnlyList<SyntaxToken> Tokens { get; }

    /// <summary>Comment and doc-comment text, whitespace collapsed so re-indenting a comment is not a comment change.</summary>
    public IReadOnlyList<string> Comments { get; }

    /// <summary>Preprocessor directives, disabled text and skipped tokens: anything that changes what compiles.</summary>
    public IReadOnlyList<string> Structure { get; }

    /// <summary>Null when the text does not parse cleanly: a file with errors proves nothing.</summary>
    public static CodeFingerprint? Of(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text, ParseOptions);
        var root = tree.GetRoot();
        if (root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return null;
        }

        var tokens = new List<SyntaxToken>();
        var comments = new List<string>();
        var structure = new List<string>();
        foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
        {
            Collect(token.LeadingTrivia, comments, structure);
            if (!token.IsKind(SyntaxKind.EndOfFileToken))
            {
                tokens.Add(token);
            }

            Collect(token.TrailingTrivia, comments, structure);
        }

        return new CodeFingerprint(root, tokens, comments, structure);
    }

    public static bool SameTokens(IReadOnlyList<SyntaxToken> a, IReadOnlyList<SyntaxToken> b) =>
        a.Count == b.Count && a.Zip(b).All(p => SameToken(p.First, p.Second));

    public static bool SameToken(SyntaxToken a, SyntaxToken b) =>
        a.RawKind == b.RawKind && string.Equals(a.Text, b.Text, StringComparison.Ordinal);

    public static string Normalise(string text) => Whitespace().Replace(text, " ").Trim();

    private static void Collect(SyntaxTriviaList trivia, List<string> comments, List<string> structure)
    {
        foreach (var t in trivia)
        {
            switch (t.Kind())
            {
                case SyntaxKind.WhitespaceTrivia:
                case SyntaxKind.EndOfLineTrivia:
                    break;
                case SyntaxKind.SingleLineCommentTrivia:
                case SyntaxKind.MultiLineCommentTrivia:
                case SyntaxKind.SingleLineDocumentationCommentTrivia:
                case SyntaxKind.MultiLineDocumentationCommentTrivia:
                    comments.Add(Normalise(t.ToFullString()));
                    break;
                default:
                    // Directives, disabled text, skipped tokens, conflict markers: all can change what compiles.
                    structure.Add(t.Kind() + ":" + Normalise(t.ToFullString()));
                    break;
            }
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
