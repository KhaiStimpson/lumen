using Lumen.Analysis.Tests.Support;
using Lumen.Domain;

namespace Lumen.Analysis.Tests;

public class TriagePipelineTests
{
    private sealed class Fixed(Func<HunkContext, HunkVerdict?> decide) : IHunkClassifier
    {
        public HunkVerdict? Classify(HunkContext context) => decide(context);
    }

    private static PullRequestSnapshot Snapshot(params string[] diffs) =>
        TestDiffs.Snapshot(string.Concat(diffs));

    [Fact]
    public void UnclaimedHunkInModifiedFileIsBehaviourChange()
    {
        var snapshot = Snapshot(TestDiffs.RewrittenFile("src/Order.cs", "class A { int X() => 1; }", "class A { int X() => 2; }"));

        var result = TriagePipeline.Default.Run(snapshot);

        var hunk = Assert.Single(result.Hunks);
        Assert.Equal(ChangeClass.BehaviourChange, hunk.Class);
        Assert.Equal(TriageTier.WorthALook, hunk.Tier);
        Assert.NotEmpty(hunk.Reasons);
        Assert.Empty(result.Groups);
    }

    [Fact]
    public void UnclaimedHunkInAddedFileIsNewCode()
    {
        var snapshot = Snapshot(TestDiffs.AddedFile("src/Fresh.cs", "class Fresh { }\nclass Other { }"));

        var hunk = Assert.Single(TriagePipeline.Default.Run(snapshot).Hunks);

        Assert.Equal(ChangeClass.NewCode, hunk.Class);
        Assert.Equal(TriageTier.Skim, hunk.Tier);
        Assert.Null(hunk.OldStart);
        Assert.Equal(1, hunk.NewStart);
        Assert.Equal(2, hunk.NewEnd);
    }

    [Theory]
    [InlineData("src/Api.g.cs")]
    [InlineData("package-lock.json")]
    [InlineData("src/Data/Migrations/20240101_Init.cs")]
    public void MechanicalFilesBecomeGeneratedSkip(string path)
    {
        var snapshot = Snapshot(TestDiffs.AddedFile(path, "line one\nline two"));

        var hunk = Assert.Single(TriagePipeline.Default.Run(snapshot).Hunks);

        Assert.Equal(ChangeClass.Generated, hunk.Class);
        Assert.Equal(TriageTier.Skip, hunk.Tier);
        Assert.Equal(snapshot.Files[0].Mechanical.Reason, Assert.Single(hunk.Reasons));
    }

    [Fact]
    public void FirstProofWins()
    {
        var snapshot = Snapshot(TestDiffs.RewrittenFile("src/A.cs", "a", "b"));
        var first = new Fixed(_ => new(ChangeClass.Formatting, TriageTier.Skip, ["first"]));
        var second = new Fixed(_ => new(ChangeClass.Rename, TriageTier.Skip, ["second"]));

        var hunk = Assert.Single(new TriagePipeline([first, second]).Run(snapshot).Hunks);

        Assert.Equal(ChangeClass.Formatting, hunk.Class);
        Assert.Equal(["first"], hunk.Reasons);
    }

    [Fact]
    public void DecliningClassifierFallsThroughToTheNext()
    {
        var snapshot = Snapshot(TestDiffs.RewrittenFile("src/A.cs", "a", "b"));
        var decline = new Fixed(_ => null);
        var claim = new Fixed(_ => new(ChangeClass.CommentsOnly, TriageTier.Skip, ["comments"]));

        var hunk = Assert.Single(new TriagePipeline([decline, claim]).Run(snapshot).Hunks);

        Assert.Equal(ChangeClass.CommentsOnly, hunk.Class);
    }

    [Fact]
    public void ThrowingClassifierProvesNothing()
    {
        var snapshot = Snapshot(TestDiffs.RewrittenFile("src/A.cs", "a", "b"));
        var boom = new Fixed(_ => throw new InvalidOperationException("parse error"));

        var hunk = Assert.Single(new TriagePipeline([boom]).Run(snapshot).Hunks);

        Assert.Equal(ChangeClass.BehaviourChange, hunk.Class);
        Assert.NotEqual(TriageTier.Skip, hunk.Tier);
    }

    [Fact]
    public void SharedGroupClaimsBecomeOneGroupAcrossFiles()
    {
        var snapshot = Snapshot(
            TestDiffs.RewrittenFile("src/A.cs", "Foo x;", "Bar x;"),
            TestDiffs.RewrittenFile("src/B.cs", "Foo y;", "Bar y;"),
            TestDiffs.RewrittenFile("src/C.cs", "other", "changed"));
        var rename = new Fixed(c => c.File.Path == "src/C.cs"
            ? null
            : new(ChangeClass.Rename, TriageTier.Skip, ["pure rename `Foo`→`Bar`"], new GroupClaim("Foo>Bar", "Renamed `Foo`→`Bar`")));

        var result = new TriagePipeline([rename]).Run(snapshot);

        var group = Assert.Single(result.Groups);
        Assert.Equal("Renamed `Foo`→`Bar`", group.Title);
        Assert.Equal(ChangeClass.Rename, group.Class);
        Assert.Equal(["src/A.cs", "src/B.cs"], group.Members.Select(m => m.Path));
        Assert.All(group.Members, m => Assert.Equal(group.Id, m.GroupId));
        Assert.Null(result.Hunks.Single(h => h.Path == "src/C.cs").GroupId);
    }

    [Fact]
    public void GroupIdsComeFromWhatTheGroupIsNotItsPosition()
    {
        var claim = new Fixed(c => new(ChangeClass.Rename, TriageTier.Skip, ["r"], new GroupClaim(c.File.Path == "src/A.cs" ? "k1" : "k2", "t")));
        var both = new TriagePipeline([claim]).Run(Snapshot(TestDiffs.RewrittenFile("src/A.cs", "a", "b"), TestDiffs.RewrittenFile("src/B.cs", "a", "b")));
        var onlyB = new TriagePipeline([claim]).Run(Snapshot(TestDiffs.RewrittenFile("src/B.cs", "a", "b")));

        Assert.Equal(TriagePipeline.IdFor("k2"), both.Groups.Single(g => g.Members[0].Path == "src/B.cs").Id);
        Assert.Equal(TriagePipeline.IdFor("k2"), Assert.Single(onlyB.Groups).Id);
        Assert.NotEqual(TriagePipeline.IdFor("k1"), TriagePipeline.IdFor("k2"));
    }

    [Fact]
    public void SpansCoverChangedLinesNotContext()
    {
        const string diff = "diff --git a/src/A.cs b/src/A.cs\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/src/A.cs\n" +
            "+++ b/src/A.cs\n" +
            "@@ -8,7 +8,7 @@\n" +
            " ctx1\n ctx2\n ctx3\n" +
            "-old line\n" +
            "+new line\n" +
            " ctx4\n ctx5\n ctx6\n";

        var hunk = Assert.Single(TriagePipeline.Default.Run(Snapshot(diff)).Hunks);

        Assert.Equal((11, 11, 11, 11), (hunk.OldStart, hunk.OldEnd, hunk.NewStart, hunk.NewEnd));
        Assert.Equal(2, hunk.ChangedLines);
    }

    [Fact]
    public void EmptySnapshotGivesEmptyResult()
    {
        var result = TriagePipeline.Default.Run(TestDiffs.Snapshot(Array.Empty<ChangedFile>()));

        Assert.Empty(result.Hunks);
        Assert.Empty(result.Groups);
    }
}
