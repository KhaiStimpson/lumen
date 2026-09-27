using Lumen.Domain;

namespace Lumen.Analysis.Tests;

public sealed class UnifiedDiffParserTests
{
    private static string Diff(params string[] lines) => string.Join('\n', lines) + "\n";

    [Fact]
    public void ParsesModifiedFileWithLineNumbers()
    {
        var diff = Diff(
            "diff --git a/src/Foo.cs b/src/Foo.cs",
            "index 83db48f..bf269f4 100644",
            "--- a/src/Foo.cs",
            "+++ b/src/Foo.cs",
            "@@ -10,4 +10,5 @@ public class Foo",
            " context10",
            "-removed11",
            "+added11",
            "+added12",
            " context12",
            " context13");

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal("src/Foo.cs", file.Path);
        Assert.Null(file.OldPath);
        Assert.Equal(FileChangeKind.Modified, file.Kind);
        Assert.False(file.IsBinary);
        Assert.Equal(2, file.Additions);
        Assert.Equal(1, file.Deletions);

        var hunk = Assert.Single(file.Hunks);
        Assert.Equal((10, 4, 10, 5), (hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount));
        Assert.Equal("@@ -10,4 +10,5 @@ public class Foo", hunk.Header);
        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Context, 10, 10, "context10"),
                new DiffLine(DiffLineKind.Removed, 11, null, "removed11"),
                new DiffLine(DiffLineKind.Added, null, 11, "added11"),
                new DiffLine(DiffLineKind.Added, null, 12, "added12"),
                new DiffLine(DiffLineKind.Context, 12, 13, "context12"),
                new DiffLine(DiffLineKind.Context, 13, 14, "context13"),
            ],
            hunk.Lines);
    }

    [Fact]
    public void ParsesMultipleHunksIndependently()
    {
        var diff = Diff(
            "diff --git a/a.txt b/a.txt",
            "--- a/a.txt",
            "+++ b/a.txt",
            "@@ -1,2 +1,2 @@",
            "-one",
            "+ONE",
            " two",
            "@@ -20,2 +20,3 @@ section",
            " twenty",
            "+inserted",
            " twentyone");

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal(2, file.Hunks.Count);
        Assert.Equal([1, 2], file.Hunks[0].Lines.Where(l => l.NewNumber is not null).Select(l => l.NewNumber!.Value));
        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Context, 20, 20, "twenty"),
                new DiffLine(DiffLineKind.Added, null, 21, "inserted"),
                new DiffLine(DiffLineKind.Context, 21, 22, "twentyone"),
            ],
            file.Hunks[1].Lines);
    }

    [Fact]
    public void ParsesSeveralFilesInOneDiff()
    {
        var diff = Diff(
            "diff --git a/a.cs b/a.cs",
            "--- a/a.cs",
            "+++ b/a.cs",
            "@@ -1 +1 @@",
            "-x",
            "+y",
            "diff --git a/b.cs b/b.cs",
            "--- a/b.cs",
            "+++ b/b.cs",
            "@@ -3 +3,2 @@",
            " z",
            "+w");

        var files = UnifiedDiffParser.Parse(diff);

        Assert.Equal(["a.cs", "b.cs"], files.Select(f => f.Path));
        Assert.Equal(4, files[1].Hunks[0].Lines[1].NewNumber);
    }

    [Fact]
    public void ParsesAddedFile()
    {
        var diff = Diff(
            "diff --git a/src/New.cs b/src/New.cs",
            "new file mode 100644",
            "index 0000000..e69de29",
            "--- /dev/null",
            "+++ b/src/New.cs",
            "@@ -0,0 +1,3 @@",
            "+line1",
            "+line2",
            "+line3");

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal("src/New.cs", file.Path);
        Assert.Null(file.OldPath);
        Assert.Equal(FileChangeKind.Added, file.Kind);
        Assert.Equal(3, file.Additions);
        Assert.Equal([1, 2, 3], file.Hunks[0].Lines.Select(l => l.NewNumber!.Value));
        Assert.All(file.Hunks[0].Lines, l => Assert.Null(l.OldNumber));
    }

    [Fact]
    public void ParsesDeletedFile()
    {
        var diff = Diff(
            "diff --git a/src/Old.cs b/src/Old.cs",
            "deleted file mode 100644",
            "index e69de29..0000000",
            "--- a/src/Old.cs",
            "+++ /dev/null",
            "@@ -1,2 +0,0 @@",
            "-line1",
            "-line2");

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal("src/Old.cs", file.Path);
        Assert.Equal("src/Old.cs", file.OldPath);
        Assert.Equal(FileChangeKind.Deleted, file.Kind);
        Assert.Equal(2, file.Deletions);
        Assert.Equal([1, 2], file.Hunks[0].Lines.Select(l => l.OldNumber!.Value));
    }

    [Fact]
    public void ParsesPureRename()
    {
        var diff = Diff(
            "diff --git a/src/Old.cs b/src/New.cs",
            "similarity index 100%",
            "rename from src/Old.cs",
            "rename to src/New.cs");

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal("src/New.cs", file.Path);
        Assert.Equal("src/Old.cs", file.OldPath);
        Assert.Equal(FileChangeKind.Renamed, file.Kind);
        Assert.Empty(file.Hunks);
    }

    [Fact]
    public void ParsesRenameWithEdits()
    {
        var diff = Diff(
            "diff --git a/src/Old.cs b/src/Sub/New.cs",
            "similarity index 90%",
            "rename from src/Old.cs",
            "rename to src/Sub/New.cs",
            "index 1..2 100644",
            "--- a/src/Old.cs",
            "+++ b/src/Sub/New.cs",
            "@@ -1 +1 @@",
            "-a",
            "+b");

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal("src/Sub/New.cs", file.Path);
        Assert.Equal("src/Old.cs", file.OldPath);
        Assert.Equal(FileChangeKind.Renamed, file.Kind);
        Assert.Equal((1, 1), (file.Additions, file.Deletions));
    }

    [Fact]
    public void IgnoresNoNewlineMarkers()
    {
        var diff = Diff(
            "diff --git a/a.txt b/a.txt",
            "--- a/a.txt",
            "+++ b/a.txt",
            "@@ -1,2 +1,2 @@",
            " first",
            "-old",
            "\\ No newline at end of file",
            "+new",
            "\\ No newline at end of file");

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Context, 1, 1, "first"),
                new DiffLine(DiffLineKind.Removed, 2, null, "old"),
                new DiffLine(DiffLineKind.Added, null, 2, "new"),
            ],
            file.Hunks[0].Lines);
    }

    [Fact]
    public void HunkHeaderWithoutCountsDefaultsToOne()
    {
        var diff = Diff(
            "diff --git a/a.txt b/a.txt",
            "--- a/a.txt",
            "+++ b/a.txt",
            "@@ -1 +1 @@",
            "-x",
            "+y");

        var hunk = Assert.Single(Assert.Single(UnifiedDiffParser.Parse(diff)).Hunks);

        Assert.Equal((1, 1, 1, 1), (hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount));
        Assert.Equal(2, hunk.Lines.Count);
    }

    [Fact]
    public void RemovedAndAddedLinesThatLookLikeFileHeadersStayInTheHunk()
    {
        // "--- x" is a removed line whose content is "-- x" (e.g. a SQL comment); "+++ y" is an added "++ y".
        var diff = Diff(
            "diff --git a/db.sql b/db.sql",
            "--- a/db.sql",
            "+++ b/db.sql",
            "@@ -1,2 +1,2 @@",
            "--- old comment",
            "+++ new comment",
            " select 1;");

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal("db.sql", file.Path);
        Assert.Null(file.OldPath);
        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Removed, 1, null, "-- old comment"),
                new DiffLine(DiffLineKind.Added, null, 1, "++ new comment"),
                new DiffLine(DiffLineKind.Context, 2, 2, "select 1;"),
            ],
            file.Hunks[0].Lines);
    }

    [Fact]
    public void LinesAfterAClosedHunkAreNotAddedToIt()
    {
        var diff = Diff(
            "diff --git a/a.txt b/a.txt",
            "--- a/a.txt",
            "+++ b/a.txt",
            "@@ -1 +1 @@",
            "-x",
            "+y",
            "+stray");

        var hunk = Assert.Single(Assert.Single(UnifiedDiffParser.Parse(diff)).Hunks);

        Assert.Equal(2, hunk.Lines.Count);
    }

    [Fact]
    public void ToleratesTrailingEmptyLineAndCrLf()
    {
        var diff = "diff --git a/a.txt b/a.txt\r\n--- a/a.txt\r\n+++ b/a.txt\r\n@@ -1,1 +1,2 @@\r\n a\r\n+b\r\n\r\n";

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Context, 1, 1, "a"),
                new DiffLine(DiffLineKind.Added, null, 2, "b"),
            ],
            file.Hunks[0].Lines);
    }

    [Fact]
    public void EmptyInputProducesNoFiles()
    {
        Assert.Empty(UnifiedDiffParser.Parse(""));
        Assert.Empty(UnifiedDiffParser.Parse("\n"));
    }

    [Fact]
    public void ParsesBinaryFile()
    {
        var diff = Diff(
            "diff --git a/img/logo.png b/img/logo.png",
            "new file mode 100644",
            "index 0000000..abcdef0",
            "Binary files /dev/null and b/img/logo.png differ");

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal("img/logo.png", file.Path);
        Assert.True(file.IsBinary);
        Assert.Equal(FileChangeKind.Added, file.Kind);
        Assert.Empty(file.Hunks);
    }

    [Fact]
    public void ParsesGitBinaryPatch()
    {
        var diff = Diff(
            "diff --git a/img/logo.png b/img/logo.png",
            "index 1111111..2222222 100644",
            "GIT binary patch",
            "literal 10",
            "zcmV+b0RR91",
            "",
            "literal 8",
            "zcmV+b0RR91",
            "");

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.True(file.IsBinary);
        Assert.Equal(FileChangeKind.Modified, file.Kind);
        Assert.Empty(file.Hunks);
    }

    [Fact]
    public void StripsQuotesFromQuotedPaths()
    {
        var diff = Diff(
            "diff --git \"a/src/My File.cs\" \"b/src/My File.cs\"",
            "--- \"a/src/My File.cs\"",
            "+++ \"b/src/My File.cs\"",
            "@@ -1 +1 @@",
            "-x",
            "+y");

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal("src/My File.cs", file.Path);
    }

    [Fact]
    public void TakesPathFromGitHeaderWhenNoFileHeadersArePresent()
    {
        var diff = Diff(
            "diff --git \"a/docs/My Doc.bin\" \"b/docs/My Doc.bin\"",
            "index 1111111..2222222 100644",
            "Binary files \"a/docs/My Doc.bin\" and \"b/docs/My Doc.bin\" differ");

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal("docs/My Doc.bin", file.Path);
        Assert.True(file.IsBinary);
    }

    [Fact]
    public void DecodesEscapesInQuotedPaths()
    {
        var diff = Diff(
            "diff --git \"a/docs/caf\\303\\251.md\" \"b/docs/caf\\303\\251.md\"",
            "--- \"a/docs/caf\\303\\251.md\"",
            "+++ \"b/docs/caf\\303\\251.md\"",
            "@@ -1 +1 @@",
            "-x",
            "+y");

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal("docs/café.md", file.Path);
    }

    [Fact]
    public void ChangedFileExposesDiffLinesForAnchoring()
    {
        var files = ChangedFiles.FromUnifiedDiff(Diff(
            "diff --git a/a.cs b/a.cs",
            "--- a/a.cs",
            "+++ b/a.cs",
            "@@ -5,3 +5,3 @@",
            " five",
            "-six",
            "+SIX",
            " seven"));

        var file = Assert.Single(files);

        Assert.Equal([6], file.AddedLines());
        Assert.True(file.ContainsNewLine(5));
        Assert.True(file.ContainsNewLine(7));
        Assert.False(file.ContainsNewLine(8));
    }
}
