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
}

public sealed record DiffRow(DiffRowKind Kind, int OldNumber, int NewNumber, string? CardId = null);

/// <summary>
/// A unified diff flattened into editor text. Each document line maps to one <see cref="DiffRow"/>; review cards
/// get their own placeholder line directly under the code they refer to, so the editor stays virtualised.
/// </summary>
public sealed class DiffDocument
{
    public const char CardPlaceholder = '￼';

    private readonly Dictionary<int, int> _newLineToDocumentLine = [];
    private readonly Dictionary<string, int> _cardLines = new(StringComparer.Ordinal);

    private DiffDocument(string path, string text, IReadOnlyList<DiffRow> rows)
    {
        Path = path;
        Text = text;
        Rows = rows;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].NewNumber > 0)
            {
                _newLineToDocumentLine.TryAdd(rows[i].NewNumber, i + 1);
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

    /// <param name="cards">Review point ids and the head-side line each card sits under.</param>
    /// <param name="headText">
    /// The whole file at the PR head. When given, the unchanged code between hunks is shown as context and hunk
    /// headers are dropped, so the diff reads in place within the full file. Ignored if it can't hold the diff's
    /// head-side lines (e.g. a stale or partial copy).
    /// </param>
    public static DiffDocument Build(FileDiff diff, IEnumerable<(string Id, int NewLine)> cards, string? headText = null)
    {
        var cardsByLine = cards
            .GroupBy(c => c.NewLine)
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

        void AppendCode(DiffRowKind kind, int oldNumber, int newNumber, string content)
        {
            Append(new DiffRow(kind, oldNumber, newNumber), content.Replace('\t', ' '));

            if (newNumber > 0 && cardsByLine.Remove(newNumber, out var ids))
            {
                foreach (var id in ids)
                {
                    Append(new DiffRow(DiffRowKind.Card, 0, 0, id), CardPlaceholder.ToString());
                }
            }
        }

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
                    AppendCode(DiffRowKind.Context, nextNew + delta, nextNew, head[nextNew - 1]);
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
                AppendCode(DiffRowKind.Context, nextNew + delta, nextNew, head[nextNew - 1]);
            }
        }

        return new DiffDocument(diff.Path, text.ToString(), rows);
    }

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
