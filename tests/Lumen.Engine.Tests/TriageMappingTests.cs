using Lumen.Analysis;
using Lumen.Engine.Mapping;
using C = Lumen.Contracts;
using D = Lumen.Domain;

namespace Lumen.Engine.Tests;

public class TriageMappingTests
{
    private static D.HunkTriage Hunk(string path, D.ChangeClass cls, D.TriageTier tier, int lines, string? group = null, int? oldStart = 3, int? newStart = 4) => new()
    {
        Path = path,
        OldStart = oldStart,
        OldEnd = oldStart + 1,
        NewStart = newStart,
        NewEnd = newStart + 2,
        Class = cls,
        Tier = tier,
        Reasons = [$"{cls} reason"],
        ChangedLines = lines,
        GroupId = group,
    };

    private static D.TriageResult Triage()
    {
        var a = Hunk("src/A.cs", D.ChangeClass.Rename, D.TriageTier.Skip, 4, "g0");
        var b = Hunk("src/B.cs", D.ChangeClass.Rename, D.TriageTier.Skip, 2, "g0");
        var c = Hunk("src/B.cs", D.ChangeClass.BehaviourChange, D.TriageTier.Critical, 5);
        var d = Hunk("src/New.cs", D.ChangeClass.NewCode, D.TriageTier.Skim, 30, oldStart: null);
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
    public void EveryClassAndTierMapsBothWays()
    {
        foreach (var cls in Enum.GetValues<D.ChangeClass>())
        {
            Assert.Equal(cls, ProtoMapper.FromProto(ProtoMapper.ToProto(cls)));
        }

        foreach (var tier in Enum.GetValues<D.TriageTier>())
        {
            Assert.Equal(tier, ProtoMapper.FromProto(ProtoMapper.ToProto(tier)));
        }
    }

    [Fact]
    public void UnsetWireValuesAreNeverMechanical()
    {
        var hunk = ProtoMapper.FromProto(new C.HunkTriage { Path = "x.cs" });

        Assert.Equal(D.ChangeClass.BehaviourChange, hunk.Class);
        Assert.Equal(D.TriageTier.WorthALook, hunk.Tier);
    }

    [Fact]
    public void FilesCarryLinesPerTierAndGroupsTheirSize()
    {
        var ready = ProtoMapper.ToProto(Triage(), Snapshot(), new HashSet<string> { "src/A.cs" }, new HashSet<string> { "g0" });

        var b = ready.Files.Single(f => f.Path == "src/B.cs");
        Assert.Equal((0, 0, 0, 2, 5), (b.TierLines.WorthALook, b.TierLines.Skim, 0, b.TierLines.Skip, b.TierLines.Critical));
        Assert.True(ready.Files.Single(f => f.Path == "src/A.cs").IsViewed);
        Assert.Equal(new C.TierLines(), ready.Files.Single(f => f.Path == "src/Untouched.cs").TierLines);

        var group = Assert.Single(ready.Groups);
        Assert.Equal((2, 6, true), (group.MemberCount, group.ChangedLines, group.Acknowledged));

        Assert.Equal((41, 6, 30, 5), (ready.Summary.TotalLines, ready.Summary.MechanicalLines, ready.Summary.NewCodeLines, ready.Summary.BehaviourChangeLines));
        Assert.Equal("41 lines changed: 6 proven mechanical, 30 new code, 5 changing existing behaviour", ready.Summary.Text);
        Assert.Equal((5, 0, 30, 6), (ready.Summary.TierLines.Critical, ready.Summary.TierLines.WorthALook, ready.Summary.TierLines.Skim, ready.Summary.TierLines.Skip));
    }

    private sealed class HunkComparer : IEqualityComparer<D.HunkTriage>
    {
        public static readonly HunkComparer Instance = new();

        public bool Equals(D.HunkTriage? x, D.HunkTriage? y) =>
            x is not null && y is not null &&
            (x.Path, x.OldStart, x.OldEnd, x.NewStart, x.NewEnd, x.Class, x.Tier, x.ChangedLines, x.GroupId) ==
            (y.Path, y.OldStart, y.OldEnd, y.NewStart, y.NewEnd, y.Class, y.Tier, y.ChangedLines, y.GroupId) &&
            x.Reasons.SequenceEqual(y.Reasons);

        public int GetHashCode(D.HunkTriage obj) => obj.Path.GetHashCode(StringComparison.Ordinal);
    }
}
