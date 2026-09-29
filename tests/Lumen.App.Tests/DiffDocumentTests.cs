using Lumen.App.Diff;
using Lumen.Contracts;

namespace Lumen.App.Tests;

public sealed class DiffDocumentTests
{
    private static FileDiff Sample()
    {
        var first = new DiffHunk { Header = "@@ -1,3 +1,4 @@ class Foo" };
        first.Lines.Add(new DiffLine { Kind = DiffLineKind.Context, OldNumber = 1, NewNumber = 1, Text = "a" });
        first.Lines.Add(new DiffLine { Kind = DiffLineKind.Removed, OldNumber = 2, NewNumber = 0, Text = "b" });
        first.Lines.Add(new DiffLine { Kind = DiffLineKind.Added, OldNumber = 0, NewNumber = 2, Text = "\tB" });
        first.Lines.Add(new DiffLine { Kind = DiffLineKind.Added, OldNumber = 0, NewNumber = 3, Text = "C" });
        first.Lines.Add(new DiffLine { Kind = DiffLineKind.Context, OldNumber = 3, NewNumber = 4, Text = "d" });

        var second = new DiffHunk { Header = "@@ -10,2 +11,2 @@" };
        second.Lines.Add(new DiffLine { Kind = DiffLineKind.Context, OldNumber = 10, NewNumber = 11, Text = "x" });
        second.Lines.Add(new DiffLine { Kind = DiffLineKind.Context, OldNumber = 11, NewNumber = 12, Text = "y" });

        var diff = new FileDiff { Path = "src/Foo.cs" };
        diff.Hunks.Add(first);
        diff.Hunks.Add(second);
        return diff;
    }

    [Fact]
    public void FlattensHunksIntoRowsAndMapsLines()
    {
        var doc = DiffDocument.Build(Sample(), [("p1", 3), ("p2", 12)]);

        Assert.Equal("src/Foo.cs", doc.Path);
        Assert.Equal(
            [
                DiffRowKind.HunkHeader, DiffRowKind.Context, DiffRowKind.Removed, DiffRowKind.Added, DiffRowKind.Added,
                DiffRowKind.Card, DiffRowKind.Context, DiffRowKind.HunkHeader, DiffRowKind.Context, DiffRowKind.Context,
                DiffRowKind.Card,
            ],
            doc.Rows.Select(r => r.Kind));

        var lines = doc.Text.Split('\n');
        Assert.Equal(doc.Rows.Count, lines.Length);
        Assert.Equal("@@ -1,3 +1,4 @@  class Foo", lines[0]);
        Assert.Equal("@@ -10,2 +11,2 @@", lines[7].TrimEnd());
        Assert.Equal(" B", lines[3]);
        Assert.Equal(DiffDocument.CardPlaceholder.ToString(), lines[5]);

        Assert.Equal(new DiffRow(DiffRowKind.HunkHeader, 0, 0), doc.RowAt(1));
        Assert.Equal(new DiffRow(DiffRowKind.Removed, 2, 0), doc.RowAt(3));
        Assert.Equal(new DiffRow(DiffRowKind.Added, 0, 3), doc.RowAt(5));
        Assert.Equal(new DiffRow(DiffRowKind.Context, 3, 4), doc.RowAt(7));
        Assert.Null(doc.RowAt(0));
        Assert.Null(doc.RowAt(12));

        Assert.Equal(2, doc.Additions);
        Assert.Equal(1, doc.Deletions);
        Assert.False(doc.IsEmpty);
    }

    [Fact]
    public void PlacesCardsDirectlyUnderTheirLine()
    {
        var doc = DiffDocument.Build(Sample(), [("p1", 3), ("p2", 12), ("missing", 7)]);

        Assert.Equal(6, doc.CardLine("p1"));
        Assert.Equal(doc.DocumentLineForNewLine(3) + 1, doc.CardLine("p1"));
        Assert.Equal(new DiffRow(DiffRowKind.Card, 0, 0, "p1"), doc.RowAt(6));

        Assert.Equal(11, doc.CardLine("p2"));
        Assert.Equal(doc.DocumentLineForNewLine(12) + 1, doc.CardLine("p2"));

        // A card for a line the diff doesn't show has nowhere to go.
        Assert.Null(doc.CardLine("missing"));
        Assert.DoesNotContain(doc.Rows, r => r.CardId == "missing");
    }

    [Fact]
    public void DocumentLineForNewLineFallsForwardToNextShownLine()
    {
        var doc = DiffDocument.Build(Sample(), []);

        Assert.Equal(2, doc.DocumentLineForNewLine(1));
        Assert.Equal(4, doc.DocumentLineForNewLine(2));
        Assert.Equal(6, doc.DocumentLineForNewLine(4));

        // Lines 5-10 fall between the hunks: the next shown line is 11 (document line 8, after the second header).
        Assert.Equal(8, doc.DocumentLineForNewLine(5));
        Assert.Equal(8, doc.DocumentLineForNewLine(11));
        Assert.Equal(9, doc.DocumentLineForNewLine(12));
        Assert.Null(doc.DocumentLineForNewLine(13));
    }

    [Fact]
    public void FullFileShowsUnchangedCodeBetweenHunksWithoutHeaders()
    {
        // Head has 14 lines; the sample's hunks cover 1-4 and 11-12.
        var head = string.Join("\r\n", Enumerable.Range(1, 14).Select(n => n switch
        {
            1 => "a", 2 => "B", 3 => "C", 4 => "d", 11 => "x", 12 => "y",
            _ => "h" + n,
        })) + "\r\n";

        var doc = DiffDocument.Build(Sample(), [("p1", 3), ("p2", 7)], head);

        Assert.DoesNotContain(doc.Rows, r => r.Kind == DiffRowKind.HunkHeader);
        var code = doc.Rows.Where(r => r.Kind != DiffRowKind.Card).ToList();

        // Every head line appears once, in order, plus the one removed line.
        Assert.Equal(Enumerable.Range(1, 14), code.Where(r => r.NewNumber > 0).Select(r => r.NewNumber));
        Assert.Equal(new DiffRow(DiffRowKind.Removed, 2, 0), code[1]);

        // Between the hunks the old side is one line behind (one removed, two added); after the last it stays so.
        Assert.Equal(new DiffRow(DiffRowKind.Context, 4, 5), code.Single(r => r.NewNumber == 5));
        Assert.Equal(new DiffRow(DiffRowKind.Context, 9, 10), code.Single(r => r.NewNumber == 10));
        Assert.Equal(new DiffRow(DiffRowKind.Context, 13, 14), code.Single(r => r.NewNumber == 14));

        var lines = doc.Text.Split('\n');
        Assert.Equal(doc.Rows.Count, lines.Length);
        Assert.Equal("h5", lines[doc.DocumentLineForNewLine(5)!.Value - 1]);
        Assert.Equal("h14", lines[^1]);

        // Cards can now sit under lines outside the hunks.
        Assert.Equal(doc.DocumentLineForNewLine(7) + 1, doc.CardLine("p2"));
        Assert.Equal(doc.DocumentLineForNewLine(3) + 1, doc.CardLine("p1"));
    }

    [Fact]
    public void FullFileWithPureDeletionHunkKeepsLineBeforeIt()
    {
        var hunk = new DiffHunk { OldStart = 3, OldCount = 1, NewStart = 2, NewCount = 0, Header = "@@ -3 +2,0 @@" };
        hunk.Lines.Add(new DiffLine { Kind = DiffLineKind.Removed, OldNumber = 3, NewNumber = 0, Text = "gone" });
        var diff = new FileDiff { Path = "a.txt" };
        diff.Hunks.Add(hunk);

        var doc = DiffDocument.Build(diff, [], "one\ntwo\nthree");

        Assert.Equal(
            [
                new DiffRow(DiffRowKind.Context, 1, 1), new DiffRow(DiffRowKind.Context, 2, 2),
                new DiffRow(DiffRowKind.Removed, 3, 0), new DiffRow(DiffRowKind.Context, 4, 3),
            ],
            doc.Rows);
        Assert.Equal("one\ntwo\ngone\nthree", doc.Text);
    }

    [Fact]
    public void FullFileFallsBackToHunksWhenHeadTextIsTooShort()
    {
        var doc = DiffDocument.Build(Sample(), [], "a\nB");

        Assert.Equal(DiffDocument.Build(Sample(), []).Text, doc.Text);
        Assert.Equal(DiffRowKind.HunkHeader, doc.Rows[0].Kind);
    }

    [Fact]
    public void EmptyDiffHasNoRows()
    {
        var doc = DiffDocument.Build(new FileDiff { Path = "a.txt" }, [("p", 1)]);

        Assert.True(doc.IsEmpty);
        Assert.Equal("", doc.Text);
        Assert.Null(doc.CardLine("p"));
        Assert.Null(doc.DocumentLineForNewLine(1));
    }
    [Fact]
    public void CollapsedFoldReplacesItsChangedLinesWithOneBadgeRow()
    {
        var fold = new DiffFold("f1", 2, 2, 2, 3, Expanded: false);

        var doc = DiffDocument.Build(Sample(), [], folds: [fold]);

        Assert.Equal(
            [DiffRowKind.HunkHeader, DiffRowKind.Context, DiffRowKind.Card, DiffRowKind.Context, DiffRowKind.HunkHeader, DiffRowKind.Context, DiffRowKind.Context],
            doc.Rows.Select(r => r.Kind));
        Assert.Equal("fold:f1", doc.Rows[2].CardId);
        Assert.Equal(0, doc.Additions);
        Assert.Equal(3, doc.CardLine("fold:f1"));
    }

    [Fact]
    public void ExpandedFoldKeepsItsBadgeAboveTheLines()
    {
        var doc = DiffDocument.Build(Sample(), [], folds: [new DiffFold("f1", 2, 2, 2, 3, Expanded: true)]);

        Assert.Equal(
            [DiffRowKind.HunkHeader, DiffRowKind.Context, DiffRowKind.Card, DiffRowKind.Removed, DiffRowKind.Added, DiffRowKind.Added, DiffRowKind.Context],
            doc.Rows.Take(7).Select(r => r.Kind));
        Assert.Equal(2, doc.Additions);
    }

    [Fact]
    public void AFoldCoversOnlyItsOwnSpan()
    {
        var doc = DiffDocument.Build(Sample(), [], folds: [new DiffFold("f1", 0, 0, 3, 3, Expanded: false)]);

        Assert.Equal(
            [DiffRowKind.HunkHeader, DiffRowKind.Context, DiffRowKind.Removed, DiffRowKind.Added, DiffRowKind.Card, DiffRowKind.Context],
            doc.Rows.Take(6).Select(r => r.Kind));
    }
}
