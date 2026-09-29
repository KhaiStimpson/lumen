using Lumen.Analysis;
using Lumen.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lumen.Roslyn.Triage;

/// <summary>
/// Proves lines are moved code: a member deleted wholesale in one place and added wholesale in another (same file or
/// across files) with an identical token stream. A member moved with edits is paired by name and kind, and only the
/// lines that differ are left as behaviour changes. A hunk mixing moved and other lines is reported in parts.
/// </summary>
public sealed class MoveClassifier : IHunkClassifier
{
    public HunkVerdict? Classify(HunkContext context)
    {
        var index = MoveIndex.For(context);
        var changed = context.Hunk.Lines.Where(l => l.Kind != DiffLineKind.Context).ToList();
        var labels = changed.Select(l => l.Kind == DiffLineKind.Removed
            ? index.BaseLabel(context.File.Path, l.OldNumber)
            : index.HeadLabel(context.File.Path, l.NewNumber)).ToList();
        if (!labels.Any(l => l is { Edited: false }))
        {
            return null;
        }

        // Runs of lines with the same label; blank lines join the run they sit in.
        var runs = new List<(MoveLabel? Label, List<DiffLine> Lines)>();
        var pendingBlank = new List<DiffLine>();
        for (var i = 0; i < changed.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(changed[i].Text))
            {
                pendingBlank.Add(changed[i]);
                continue;
            }

            if (runs.Count > 0 && Equals(runs[^1].Label, labels[i]))
            {
                runs[^1].Lines.AddRange(pendingBlank);
                runs[^1].Lines.Add(changed[i]);
            }
            else
            {
                runs.Add((labels[i], [.. pendingBlank, changed[i]]));
            }

            pendingBlank.Clear();
        }

        if (runs.Count == 0)
        {
            return null;
        }

        runs[^1].Lines.AddRange(pendingBlank);

        var parts = runs.Select(r => Part(r.Label, r.Lines, context.File)).ToList();
        return parts.Count == 1
            ? new(parts[0].Class, parts[0].Tier, parts[0].Reasons, parts[0].Group)
            : new(ChangeClass.Move, parts.Min(p => p.Tier), ["moved code"]) { Parts = parts };
    }

    private static HunkPart Part(MoveLabel? label, List<DiffLine> lines, ChangedFile file) => label switch
    {
        { Edited: false } l => new(ChangeClass.Move, l.Pair.Tier, l.Pair.Reasons, new GroupClaim(l.Pair.Key, l.Pair.Title), lines),
        { Edited: true } l => new(ChangeClass.BehaviourChange, TriageTier.WorthALook, [$"edited while moving `{l.Pair.Name}`"], null, lines),
        null when file.Kind == FileChangeKind.Added => new(ChangeClass.NewCode, TriageTier.Skim, ["new file"], null, lines),
        null => new(ChangeClass.BehaviourChange, TriageTier.WorthALook, ["changes existing code"], null, lines),
    };
}

internal sealed record MovePair(string Key, string Name, string Title, TriageTier Tier, IReadOnlyList<string> Reasons);

internal sealed record MoveLabel(MovePair Pair, bool Edited);

/// <summary>Members deleted wholesale and added wholesale across the pull request, paired into moves, with every line labelled.</summary>
internal sealed class MoveIndex
{
    private const double EditedMoveSimilarity = 0.5;

    private readonly Dictionary<(string Path, int Line), MoveLabel> baseLabels = [];
    private readonly Dictionary<(string Path, int Line), MoveLabel> headLabels = [];

    private MoveIndex(HunkContext context)
    {
        var removed = new List<Member>();
        var added = new List<Member>();
        foreach (var file in context.Snapshot.Files.Where(f => CSharpHunkProof.IsCSharp(f.Path)))
        {
            var removedLines = file.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind == DiffLineKind.Removed).Select(l => l.OldNumber!.Value).ToHashSet();
            var addedLines = file.AddedLines().ToHashSet();
            if (context.Sources.Base(file.Path) is { } baseText && removedLines.Count > 0 &&
                context.Workspace.GetOrAdd("fingerprint:base:" + file.Path, () => CodeFingerprint.Of(baseText)) is { } before)
            {
                removed.AddRange(Wholly(before.Root, file.Path, baseText, removedLines));
            }

            if (context.Sources.Head(file.Path) is { } headText && addedLines.Count > 0 &&
                context.Workspace.GetOrAdd("fingerprint:head:" + file.Path, () => CodeFingerprint.Of(headText)) is { } after)
            {
                added.AddRange(Wholly(after.Root, file.Path, headText, addedLines));
            }
        }

        // Exact moves: the same tokens deleted in one place and added in another.
        var addedByKey = added.GroupBy(m => m.Key).ToDictionary(g => g.Key, g => new Queue<Member>(g));
        var unpairedRemoved = new List<Member>();
        foreach (var r in removed)
        {
            if (addedByKey.TryGetValue(r.Key, out var queue) && queue.Count > 0)
            {
                var d = queue.Dequeue();
                Label(r, d, edited: false);
            }
            else
            {
                unpairedRemoved.Add(r);
            }
        }

        // Edited moves: one deleted and one added member of the same kind and name, mostly the same lines.
        var unpairedAdded = addedByKey.Values.SelectMany(q => q).ToList();
        foreach (var r in unpairedRemoved)
        {
            var sameName = unpairedAdded.Where(d => d.Kind == r.Kind && d.Name == r.Name).ToList();
            if (sameName.Count != 1 || unpairedRemoved.Count(o => o.Kind == r.Kind && o.Name == r.Name) != 1)
            {
                continue;
            }

            var d = sameName[0];
            var matches = Lcs(r.Lines.Select(l => l.Text).ToList(), d.Lines.Select(l => l.Text).ToList());
            if (matches.Count >= EditedMoveSimilarity * Math.Max(r.Lines.Count, d.Lines.Count))
            {
                Label(r, d, edited: true, matches);
                unpairedAdded.Remove(d);
            }
        }
    }

    public static MoveIndex For(HunkContext context) => context.Workspace.GetOrAdd("move-index", () => new MoveIndex(context));

    public MoveLabel? BaseLabel(string path, int? line) => line is { } n && baseLabels.TryGetValue((path, n), out var l) ? l : null;

    public MoveLabel? HeadLabel(string path, int? line) => line is { } n && headLabels.TryGetValue((path, n), out var l) ? l : null;

    private void Label(Member r, Member d, bool edited, List<(int A, int B)>? matches = null)
    {
        var sameFile = r.Path == d.Path;
        var title = sameFile
            ? $"Moved `{r.Name}` within {Path.GetFileName(r.Path)}"
            : $"Moved `{r.Name}` from {Path.GetFileName(r.Path)} to {Path.GetFileName(d.Path)}";

        var (tier, caveat) = (r, d) switch
        {
            _ when r.Container != d.Container => (TriageTier.Skim, $"moved into `{d.Container}`: names in its body may bind differently"),
            _ when !sameFile && !r.Usings.SequenceEqual(d.Usings) => (TriageTier.Skim, "the destination file imports different namespaces: names may bind differently"),
            _ when r.OrderSensitive => (TriageTier.Skim, "field order decides initialisation order and struct layout"),
            _ => (TriageTier.Skip, null),
        };
        var reason = sameFile ? $"moved `{r.Name}` within the file" : $"moved `{r.Name}` from {r.Path} to {d.Path}";
        var pair = new MovePair(
            $"move:{r.Path}:{r.Lines[0].Number}>{d.Path}:{d.Lines[0].Number}",
            r.Name,
            title,
            tier,
            caveat is null ? [reason] : [reason, caveat]);

        var matchedA = matches?.Select(m => m.A).ToHashSet();
        var matchedB = matches?.Select(m => m.B).ToHashSet();
        for (var i = 0; i < r.Lines.Count; i++)
        {
            baseLabels[(r.Path, r.Lines[i].Number)] = new MoveLabel(pair, edited && !matchedA!.Contains(i));
        }

        for (var i = 0; i < d.Lines.Count; i++)
        {
            headLabels[(d.Path, d.Lines[i].Number)] = new MoveLabel(pair, edited && !matchedB!.Contains(i));
        }
    }

    /// <summary>Outermost members whose every non-blank line is among <paramref name="changedLines"/>.</summary>
    private static IEnumerable<Member> Wholly(SyntaxNode root, string path, string text, HashSet<int> changedLines)
    {
        // A member moved into or out of an #if region compiles under different conditions: no moves in such files.
        if (root.DescendantTrivia().Any(t => t.IsKind(SyntaxKind.IfDirectiveTrivia)))
        {
            yield break;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var source = root.SyntaxTree.GetText();
        var covered = new HashSet<SyntaxNode>();
        foreach (var node in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            if (node is BaseNamespaceDeclarationSyntax or GlobalStatementSyntax or EnumMemberDeclarationSyntax ||
                node.Ancestors().Any(covered.Contains))
            {
                continue;
            }

            var tree = node.SyntaxTree;
            var hasDirective = node.GetLeadingTrivia().Any(t => t.IsDirective || t.IsKind(SyntaxKind.DisabledTextTrivia));
            var from = hasDirective ? node.Span.Start : node.FullSpan.Start;
            if (!OwnsItsLines(node, from, source))
            {
                continue;
            }

            var start = tree.GetLineSpan(hasDirective ? node.Span : node.FullSpan).StartLinePosition.Line + 1;
            var end = tree.GetLineSpan(node.Span).EndLinePosition.Line + 1;
            var memberLines = Enumerable.Range(start, end - start + 1)
                .Where(n => n - 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[n - 1]))
                .Select(n => new MemberLine(n, lines[n - 1].Trim()))
                .ToList();
            if (memberLines.Count == 0 || !memberLines.All(l => changedLines.Contains(l.Number)))
            {
                continue;
            }

            covered.Add(node);
            yield return new Member(
                path,
                node.Kind(),
                NameOf(node),
                string.Join("\u0001", node.DescendantTokens().Select(t => $"{t.RawKind}:{t.Text}")),
                ContainerOf(node),
                UsingsOf(root),
                node is FieldDeclarationSyntax f && (f.Parent is StructDeclarationSyntax || f.Declaration.Variables.Any(v => v.Initializer is not null)),
                memberLines);
        }
    }

    /// <summary>
    /// True when the member's lines hold nothing but the member: no other code before it on its first line, no code
    /// after it on its last line, and no trailing comment running on past its last line (which would swallow code).
    /// Only then do "its lines were deleted here and added there" and "it moved" mean the same thing.
    /// </summary>
    private static bool OwnsItsLines(SyntaxNode node, int from, Microsoft.CodeAnalysis.Text.SourceText source)
    {
        var firstLine = source.Lines.GetLineFromPosition(from);
        if (!string.IsNullOrWhiteSpace(source.ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(firstLine.Start, from))))
        {
            return false;
        }

        var trailing = node.GetTrailingTrivia();
        if (trailing.Any(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia) && !t.IsKind(SyntaxKind.SingleLineCommentTrivia)))
        {
            return false;
        }

        return node.FullSpan.End >= source.Length || (trailing.Count > 0 && trailing[^1].IsKind(SyntaxKind.EndOfLineTrivia));
    }

    private static string NameOf(MemberDeclarationSyntax node) => node switch
    {
        BaseTypeDeclarationSyntax t => t.Identifier.ValueText,
        MethodDeclarationSyntax m => m.Identifier.ValueText,
        PropertyDeclarationSyntax p => p.Identifier.ValueText,
        EventDeclarationSyntax e => e.Identifier.ValueText,
        ConstructorDeclarationSyntax c => c.Identifier.ValueText,
        DelegateDeclarationSyntax d => d.Identifier.ValueText,
        BaseFieldDeclarationSyntax f => string.Join(", ", f.Declaration.Variables.Select(v => v.Identifier.ValueText)),
        IndexerDeclarationSyntax => "this[]",
        OperatorDeclarationSyntax o => "operator " + o.OperatorToken.Text,
        ConversionOperatorDeclarationSyntax c => "operator " + c.Type,
        _ => node.Kind().ToString(),
    };

    private static string ContainerOf(SyntaxNode node)
    {
        var types = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.ValueText).Reverse();
        var ns = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).Reverse();
        var parts = ns.Concat(types).ToList();
        if (parts.Count == 0 && node.SyntaxTree.GetRoot() is CompilationUnitSyntax cu &&
            cu.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault() is { } fs)
        {
            parts.Add(fs.Name.ToString());
        }

        return string.Join(".", parts);
    }

    private static List<string> UsingsOf(SyntaxNode root) =>
        root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.ToString()).Order(StringComparer.Ordinal).ToList();

    /// <summary>Matched index pairs of a longest common subsequence.</summary>
    private static List<(int A, int B)> Lcs(List<string> a, List<string> b)
    {
        var table = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
        {
            for (var j = b.Count - 1; j >= 0; j--)
            {
                table[i, j] = a[i] == b[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var result = new List<(int, int)>();
        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (a[x] == b[y])
            {
                result.Add((x++, y++));
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return result;
    }

    private sealed record MemberLine(int Number, string Text);

    private sealed record Member(
        string Path,
        SyntaxKind Kind,
        string Name,
        string Key,
        string Container,
        IReadOnlyList<string> Usings,
        bool OrderSensitive,
        IReadOnlyList<MemberLine> Lines);
}
