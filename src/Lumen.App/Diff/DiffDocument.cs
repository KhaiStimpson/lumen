using System.Text;
using Lumen.Contracts;

namespace Lumen.App.Diff;

public enum DiffRowKind
{
    HunkHeader,
    Context,
    Added,
    Removed,

    /// <summary>A one-character placeholder line that hosts an inline review card.</summary>
    Card,

    /// <summary>Split view: the other side has a line here and this side doesn't.</summary>
    Filler,
}

/// <param name="IsOutsideDiff">Full file view: an unchanged line between hunks. GitHub can't attach comments there.</param>
public sealed record DiffRow(DiffRowKind Kind, int OldNumber, int NewNumber, string? CardId = null, bool IsOutsideDiff = false);

/// <summary>Which side of the diff a line number refers to. Right is the head; Left is the base (deleted lines).</summary>
public enum LineSide
{
    Right,
    Left,
}

/// <summary>
/// Something shown under a line: a review card, a thread, a comment draft. Line 0 puts it at the top of the file
/// (e.g. outdated threads whose line no longer exists).
/// </summary>
public sealed record CardAnchor(string Id, LineSide Side, int Line);

/// <summary>A find-in-file hit: document line (1-based), column and length.</summary>
public sealed record FindMatch(int DocumentLine, int Column, int Length);

/// <summary>
/// A run of changed rows (document lines <see cref="StartLine"/>..<see cref="EndLine"/>, 1-based). Cards inside the run
/// don't split it. <see cref="Fillers"/> counts split-view rows where the other side has the line.
/// </summary>
public sealed record DiffChange(int StartLine, int EndLine, int Added, int Removed, int Fillers);

/// <summary>A diff laid out side by side; the maps take a unified document line to each side's line.</summary>
public sealed record SplitDocument(DiffDocument Left, DiffDocument Right, IReadOnlyList<int> LeftLines, IReadOnlyList<int> RightLines);

/// <summary>
/// A unified diff flattened into editor text. Each document line maps to one <see cref="DiffRow"/>; review cards
/// get their own placeholder line directly under the code they refer to, so the editor stays virtualised.
/// </summary>
public sealed class DiffDocument
{
    public const char CardPlaceholder = '￼';

    private readonly Dictionary<int, int> _newLineToDocumentLine = [];
    private readonly Dictionary<int, int> _oldLineToDocumentLine = [];
    private readonly Dictionary<string, int> _cardLines = new(StringComparer.Ordinal);

    private DiffDocument(string path, string text, IReadOnlyList<DiffRow> rows)
    {
        Path = path;
        Text = text;
        Rows = rows;
        Changes = FindChanges(rows);
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].NewNumber > 0)
            {
                _newLineToDocumentLine.TryAdd(rows[i].NewNumber, i + 1);
            }

            if (rows[i].OldNumber > 0)
            {
                _oldLineToDocumentLine.TryAdd(rows[i].OldNumber, i + 1);
            }

            if (rows[i].CardId is { } id)
            {
                _cardLines[id] = i + 1;
            }
        }
    }

    public string Path { get; }

    public string Text { get; }

    /// <summary>Rows indexed by document line - 1.</summary>
    public IReadOnlyList<DiffRow> Rows { get; }

    public int Additions => Rows.Count(r => r.Kind == DiffRowKind.Added);

    public int Deletions => Rows.Count(r => r.Kind == DiffRowKind.Removed);

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Each run of added, removed (or, split, filler) rows, top to bottom: what the ruler and [ / ] step through.</summary>
    public IReadOnlyList<DiffChange> Changes { get; }

    public DiffRow? RowAt(int documentLine) =>
        documentLine >= 1 && documentLine <= Rows.Count ? Rows[documentLine - 1] : null;

    /// <summary>Document line showing head-side line <paramref name="newLine"/>, or the nearest one after it.</summary>
    public int? DocumentLineForNewLine(int newLine)
    {
        if (_newLineToDocumentLine.TryGetValue(newLine, out var line))
        {
            return line;
        }

        var after = _newLineToDocumentLine.Keys.Where(k => k > newLine).DefaultIfEmpty(int.MaxValue).Min();
        return after == int.MaxValue ? null : _newLineToDocumentLine[after];
    }

    public int? CardLine(string cardId) => _cardLines.TryGetValue(cardId, out var line) ? line : null;

    /// <summary>Document line showing base-side line <paramref name="oldLine"/>, if the diff shows it.</summary>
    public int? DocumentLineForOldLine(int oldLine) => _oldLineToDocumentLine.TryGetValue(oldLine, out var line) ? line : null;

    public int? DocumentLineFor(LineSide side, int line) =>
        side == LineSide.Left ? DocumentLineForOldLine(line) ?? DocumentLineForNewLine(line) : DocumentLineForNewLine(line);

    /// <summary>The line on <paramref name="side"/> is in the diff (comments may only attach there).</summary>
    public bool Contains(LineSide side, int line) =>
        side == LineSide.Left
            ? _oldLineToDocumentLine.TryGetValue(line, out var documentLine) && RowAt(documentLine) is { Kind: not DiffRowKind.Added, IsOutsideDiff: false }
            : _newLineToDocumentLine.TryGetValue(line, out var newDocumentLine) && RowAt(newDocumentLine) is { IsOutsideDiff: false };

    /// <summary>
    /// The side and line a document row offers for a comment: a removed line is on the base side, anything else with a
    /// head line number on the head side; hunk headers and cards offer nothing.
    /// </summary>
    public static (LineSide Side, int Line)? CommentTarget(DiffRow? row) => row switch
    {
        { IsOutsideDiff: true } => null,
        { Kind: DiffRowKind.Removed, OldNumber: > 0 } => (LineSide.Left, row.OldNumber),
        { Kind: DiffRowKind.Added or DiffRowKind.Context, NewNumber: > 0 } => (LineSide.Right, row.NewNumber),
        _ => null,
    };

    public static DiffDocument Build(FileDiff diff, IEnumerable<(string Id, int NewLine)> cards, string? headText = null) =>
        Build(diff, cards.Select(c => new CardAnchor(c.Id, LineSide.Right, c.NewLine)), headText);

    /// <param name="cards">Review point ids and the head-side line each card sits under.</param>
    /// <param name="headText">
    /// The whole file at the PR head. When given, the unchanged code between hunks is shown as context and hunk
    /// headers are dropped, so the diff reads in place within the full file. Ignored if it can't hold the diff's
    /// head-side lines (e.g. a stale or partial copy).
    /// </param>
    public static DiffDocument Build(FileDiff diff, IEnumerable<CardAnchor> cards, string? headText = null)
    {
        var anchors = cards.ToList();
        var cardsByLine = anchors
            .Where(c => c.Side == LineSide.Right && c.Line > 0)
            .GroupBy(c => c.Line)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList());
        var cardsByOldLine = anchors
            .Where(c => c.Side == LineSide.Left && c.Line > 0)
            .GroupBy(c => c.Line)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList());

        var rows = new List<DiffRow>();
        var text = new StringBuilder();

        void Append(DiffRow row, string content)
        {
            if (rows.Count > 0)
            {
                text.Append('\n');
            }

            rows.Add(row);
            text.Append(content);
        }

        void AppendCards(IEnumerable<string> ids)
        {
            foreach (var id in ids)
            {
                Append(new DiffRow(DiffRowKind.Card, 0, 0, id), CardPlaceholder.ToString());
            }
        }

        void AppendCode(DiffRowKind kind, int oldNumber, int newNumber, string content, bool outsideDiff = false)
        {
            Append(new DiffRow(kind, oldNumber, newNumber, IsOutsideDiff: outsideDiff), content.Replace('\t', ' '));

            // A base-side card sits under the removed (or unchanged) line it was written on.
            if (oldNumber > 0 && kind != DiffRowKind.Added && cardsByOldLine.Remove(oldNumber, out var oldIds))
            {
                AppendCards(oldIds);
            }

            if (newNumber > 0 && cardsByLine.Remove(newNumber, out var ids))
            {
                AppendCards(ids);
            }
        }

        // Cards with no line (outdated threads) lead the file.
        AppendCards(anchors.Where(c => c.Line <= 0).Select(c => c.Id));

        var head = headText is null ? null : SplitLines(headText);
        if (head is not null && diff.Hunks.SelectMany(h => h.Lines).Any(l => l.NewNumber > head.Length))
        {
            head = null;
        }

        // Next head-side line not yet shown, and old-minus-new for unchanged lines between hunks.
        var nextNew = 1;
        var delta = 0;
        foreach (var hunk in diff.Hunks.OrderBy(h => h.NewStart))
        {
            var newNumbers = hunk.Lines.Where(l => l.NewNumber > 0).Select(l => l.NewNumber).ToList();
            var oldNumbers = hunk.Lines.Where(l => l.OldNumber > 0).Select(l => l.OldNumber).ToList();
            if (head is not null)
            {
                // A pure deletion ("+N,0") sits after line N.
                var gapEnd = newNumbers.Count > 0 ? newNumbers.Min() - 1 : hunk.NewStart;
                for (; nextNew <= gapEnd; nextNew++)
                {
                    AppendCode(DiffRowKind.Context, nextNew + delta, nextNew, head[nextNew - 1], outsideDiff: true);
                }
            }
            else
            {
                Append(new DiffRow(DiffRowKind.HunkHeader, 0, 0), HunkLabel(hunk.Header));
            }

            foreach (var line in hunk.Lines)
            {
                var kind = line.Kind switch
                {
                    DiffLineKind.Added => DiffRowKind.Added,
                    DiffLineKind.Removed => DiffRowKind.Removed,
                    _ => DiffRowKind.Context,
                };
                AppendCode(kind, line.OldNumber, line.NewNumber, line.Text);
            }

            // The last line each side consumed; the next unchanged line follows both.
            var lastNew = newNumbers.Count > 0 ? newNumbers.Max() : hunk.NewStart;
            var lastOld = oldNumbers.Count > 0 ? oldNumbers.Max() : hunk.OldStart;
            nextNew = Math.Max(nextNew, lastNew + 1);
            delta = lastOld - lastNew;
        }

        if (head is not null)
        {
            for (; nextNew <= head.Length; nextNew++)
            {
                AppendCode(DiffRowKind.Context, nextNew + delta, nextNew, head[nextNew - 1], outsideDiff: true);
            }
        }

        return new DiffDocument(diff.Path, text.ToString(), rows);
    }

    /// <summary>
    /// Side by side (plan v2 M6): base on the left, head on the right, each removed run paired with the added run that
    /// replaced it. A card stays on the side its line is on; the other side gets a spacer (<c>spacer:{id}</c>) of the
    /// same height so the rows stay aligned. <see cref="SplitDocument.LeftLines"/> and
    /// <see cref="SplitDocument.RightLines"/> map each unified document line to its split line (0 when absent).
    /// </summary>
    public SplitDocument Split()
    {
        var lines = Text.Split('\n');
        var left = new List<(DiffRow Row, string Text)>();
        var right = new List<(DiffRow Row, string Text)>();
        var leftMap = new int[Rows.Count + 1];
        var rightMap = new int[Rows.Count + 1];

        void Pair((DiffRow Row, string Text, int Unified)? l, (DiffRow Row, string Text, int Unified)? r)
        {
            left.Add(l is { } a ? (a.Row, a.Text) : (new DiffRow(DiffRowKind.Filler, 0, 0), ""));
            right.Add(r is { } b ? (b.Row, b.Text) : (new DiffRow(DiffRowKind.Filler, 0, 0), ""));
            if (l is { } x)
            {
                leftMap[x.Unified] = left.Count;
            }

            if (r is { } y)
            {
                rightMap[y.Unified] = right.Count;
            }
        }

        void Cards(IEnumerable<(string Id, int Unified)> cards, bool onLeft)
        {
            foreach (var (id, unified) in cards)
            {
                var card = (new DiffRow(DiffRowKind.Card, 0, 0, id), CardPlaceholder.ToString(), unified);
                var spacer = (new DiffRow(DiffRowKind.Card, 0, 0, "spacer:" + id), CardPlaceholder.ToString(), unified);
                Pair(onLeft ? card : spacer, onLeft ? spacer : card);
            }
        }

        var i = 0;
        List<(string, int)> CardsAfter(ref int index)
        {
            var cards = new List<(string, int)>();
            while (index < Rows.Count && Rows[index].Kind == DiffRowKind.Card)
            {
                cards.Add((Rows[index].CardId!, index + 1));
                index++;
            }

            return cards;
        }

        // Cards that lead the file (outdated threads) go across the head side.
        Cards(CardsAfter(ref i), onLeft: false);
        while (i < Rows.Count)
        {
            var row = Rows[i];
            if (row.Kind is DiffRowKind.Removed or DiffRowKind.Added)
            {
                var removed = new List<(DiffRow Row, string Text, int Unified, List<(string, int)> Cards)>();
                var added = new List<(DiffRow Row, string Text, int Unified, List<(string, int)> Cards)>();
                while (i < Rows.Count && Rows[i].Kind is DiffRowKind.Removed or DiffRowKind.Added)
                {
                    var current = Rows[i];
                    var unified = i + 1;
                    var text = lines[i];
                    i++;
                    var cards = CardsAfter(ref i);
                    (current.Kind == DiffRowKind.Removed ? removed : added).Add((current, text, unified, cards));
                }

                for (var k = 0; k < Math.Max(removed.Count, added.Count); k++)
                {
                    (DiffRow, string, int)? l = k < removed.Count ? (removed[k].Row, removed[k].Text, removed[k].Unified) : null;
                    (DiffRow, string, int)? r = k < added.Count ? (added[k].Row, added[k].Text, added[k].Unified) : null;
                    Pair(l, r);
                    if (k < removed.Count)
                    {
                        Cards(removed[k].Cards, onLeft: true);
                    }

                    if (k < added.Count)
                    {
                        Cards(added[k].Cards, onLeft: false);
                    }
                }

                continue;
            }

            var single = (row, lines[i], i + 1);
            Pair(single, single);
            i++;

            // A card after an unchanged line sits on the side it was written on: base-side only if its anchor says so.
            Cards(CardsAfter(ref i), onLeft: false);
        }

        return new SplitDocument(
            Build(Path, left.Select(x => x.Row).ToList(), left.Select(x => x.Text)),
            Build(Path, right.Select(x => x.Row).ToList(), right.Select(x => x.Text)),
            leftMap,
            rightMap);
    }

    private static List<DiffChange> FindChanges(IReadOnlyList<DiffRow> rows)
    {
        var changes = new List<DiffChange>();
        int start = 0, end = 0, added = 0, removed = 0, fillers = 0;
        void Flush()
        {
            if (start > 0)
            {
                changes.Add(new DiffChange(start, end, added, removed, fillers));
            }

            start = end = added = removed = fillers = 0;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var kind = rows[i].Kind;
            if (kind is DiffRowKind.Added or DiffRowKind.Removed or DiffRowKind.Filler)
            {
                if (start == 0)
                {
                    start = i + 1;
                }

                end = i + 1;
                added += kind == DiffRowKind.Added ? 1 : 0;
                removed += kind == DiffRowKind.Removed ? 1 : 0;
                fillers += kind == DiffRowKind.Filler ? 1 : 0;
            }
            else if (kind != DiffRowKind.Card)
            {
                Flush();
            }
        }

        Flush();
        return changes;
    }

    private static DiffDocument Build(string path, IReadOnlyList<DiffRow> rows, IEnumerable<string> text) =>
        new(path, string.Join('\n', text), rows);

    private static string[] SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    /// <summary>"@@ -18,15 +20,48 @@ public class Foo" → "@@ -18,15 +20,48 @@  public class Foo".</summary>
    private static string HunkLabel(string header)
    {
        var end = header.IndexOf("@@", 2, StringComparison.Ordinal);
        return end < 0 ? header : (header[..(end + 2)] + "  " + header[(end + 2)..].Trim()).TrimEnd();
    }
}
