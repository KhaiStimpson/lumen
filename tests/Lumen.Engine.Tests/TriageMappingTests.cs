using Lumen.Analysis;
using Lumen.Engine.Mapping;
using C = Lumen.Contracts;
using D = Lumen.Domain;

namespace Lumen.Engine.Tests;

public class TriageMappingTests
{
    private static D.HunkTriage Hunk(string path, D.ChangeClass cls, D.FoldKind fold, int lines, string? group = null, int? oldStart = 3, int? newStart = 4) => new()
    {
        Path = path,
        OldStart = oldStart,
        OldEnd = oldStart + 1,
        NewStart = newStart,
        NewEnd = newStart + 2,
        Class = cls,
        Fold = fold,
        Reasons = [$"{cls} reason"],
        ChangedLines = lines,
        GroupId = group,
    };

    private static D.TriageResult Triage()
    {
        var a = Hunk("src/A.cs", D.ChangeClass.Rename, D.FoldKind.Mechanical, 4, "g0");
        var b = Hunk("src/B.cs", D.ChangeClass.Rename, D.FoldKind.Mechanical, 2, "g0");
        var c = Hunk("src/B.cs", D.ChangeClass.BehaviourChange, D.FoldKind.Open, 5);
        var d = Hunk("src/New.cs", D.ChangeClass.NewCode, D.FoldKind.Open, 30, oldStart: null);
        return new D.TriageResult([a, b, c, d], [new D.TriageGroup { Id = "g0", Class = D.ChangeClass.Rename, Title = "Renamed `Foo`→`Bar`", Members = [a, b] }]);
    }

    private static D.PullRequestSnapshot Snapshot() => new(
        new D.PullRequestKey(new D.RepositoryRef("acme", "billing"), 42),
        "b", "h", "m",
        new D.PullRequestMetadata("t", "a", "open", false, "main", "f", "https://example", null, DateTimeOffset.UnixEpoch),
        ChangedFiles.FromUnifiedDiff(
            "diff --git a/src/A.cs b/src/A.cs\nindex 1..2 100644\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,1 +1,1 @@\n-a\n+b\n" +
            "diff --git a/src/B.cs b/src/B.cs\nindex 1..2 100644\n--- a/src/B.cs\n+++ b/src/B.cs\n@@ -1,1 +1,1 @@\n-a\n+b\n" +
            "diff --git a/src/Untouched.cs b/src/Untouched.cs\nindex 1..2 100644\n--- a/src/Untouched.cs\n+++ b/src/Untouched.cs\n@@ -1,1 +1,1 @@\n-a\n+b\n"),
        []);

    [Fact]
    public void TriageRoundTripsThroughTheWire()
    {
        var original = Triage();

        var back = ProtoMapper.FromProto(ProtoMapper.ToProto(original, Snapshot(), new HashSet<string>()));

        Assert.Equal(original.Hunks, back.Hunks, HunkComparer.Instance);
        var group = Assert.Single(back.Groups);
        Assert.Equal(("g0", D.ChangeClass.Rename, "Renamed `Foo`→`Bar`"), (group.Id, group.Class, group.Title));
        Assert.Equal(["src/A.cs", "src/B.cs"], group.Members.Select(m => m.Path));
    }

    [Fact]
    public void AbsentSidesAndGroupsSurviveTheRoundTrip()
    {
        var back = ProtoMapper.FromProto(ProtoMapper.ToProto(Triage(), Snapshot(), new HashSet<string>()));

        var added = back.Hunks.Single(h => h.Path == "src/New.cs");
        Assert.Null(added.OldStart);
        Assert.Null(added.OldEnd);
        Assert.Null(added.GroupId);
    }

    [Fact]
    public void EveryClassAndFoldMapsBothWays()
    {
        foreach (var cls in Enum.GetValues<D.ChangeClass>())
        {
            Assert.Equal(cls, ProtoMapper.FromProto(ProtoMapper.ToProto(cls)));
        }

        foreach (var fold in Enum.GetValues<D.FoldKind>())
        {
            Assert.Equal(fold, ProtoMapper.FromProto(ProtoMapper.ToProto(fold)));
        }
    }

    [Fact]
    public void UnsetWireValuesNeverFold()
    {
        var hunk = ProtoMapper.FromProto(new C.HunkTriage { Path = "x.cs" });

        Assert.Equal(D.ChangeClass.BehaviourChange, hunk.Class);
        Assert.Equal(D.FoldKind.Open, hunk.Fold);
    }

    [Fact]
    public void FilesCarryLinesPerFoldAndGroupsTheirSize()
    {
        var ready = ProtoMapper.ToProto(Triage(), Snapshot(), new HashSet<string> { "src/A.cs" });

        var b = ready.Files.Single(f => f.Path == "src/B.cs");
        Assert.Equal((5, 2, 0), (b.FoldLines.Open, b.FoldLines.Mechanical, b.FoldLines.Boilerplate));
        Assert.True(ready.Files.Single(f => f.Path == "src/A.cs").IsViewed);
        Assert.Equal(new C.FoldLines(), ready.Files.Single(f => f.Path == "src/Untouched.cs").FoldLines);

        var group = Assert.Single(ready.Groups);
        Assert.Equal((2, 6), (group.MemberCount, group.ChangedLines));

        Assert.Equal((41, 6, 30, 5), (ready.Summary.TotalLines, ready.Summary.MechanicalLines, ready.Summary.NewCodeLines, ready.Summary.BehaviourChangeLines));
        Assert.Equal((35, 0), (ready.Summary.OpenLines, ready.Summary.BoilerplateLines));
    }

    [Fact]
    public void BoilerplateFoldsAreCountedApartFromMechanical()
    {
        var a = Hunk("src/Dto.cs", D.ChangeClass.NewCode, D.FoldKind.Boilerplate, 12, oldStart: null);
        var ready = ProtoMapper.ToProto(new D.TriageResult([a], []), Snapshot(), new HashSet<string>());

        Assert.Equal((12, 0, 0), (ready.Summary.BoilerplateLines, ready.Summary.MechanicalLines, ready.Summary.OpenLines));
        Assert.Equal(C.FoldKind.Boilerplate, Assert.Single(ready.Hunks).Fold);
    }

    private sealed class HunkComparer : IEqualityComparer<D.HunkTriage>
    {
        public static readonly HunkComparer Instance = new();

        public bool Equals(D.HunkTriage? x, D.HunkTriage? y) =>
            x is not null && y is not null &&
            (x.Path, x.OldStart, x.OldEnd, x.NewStart, x.NewEnd, x.Class, x.Fold, x.ChangedLines, x.GroupId) ==
            (y.Path, y.OldStart, y.OldEnd, y.NewStart, y.NewEnd, y.Class, y.Fold, y.ChangedLines, y.GroupId) &&
            x.Reasons.SequenceEqual(y.Reasons);

        public int GetHashCode(D.HunkTriage obj) => obj.Path.GetHashCode(StringComparison.Ordinal);
    }
}
