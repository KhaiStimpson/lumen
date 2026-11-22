using System.Text.Json;
using Lumen.Domain;

namespace Lumen.Analysis.Tests;

public class TriageDomainTests
{
    private static HunkTriage Hunk(string path, ChangeClass cls, TriageTier tier, string? group = null) => new()
    {
        Path = path,
        OldStart = 10,
        OldEnd = 14,
        NewStart = 12,
        NewEnd = 18,
        Class = cls,
        Tier = tier,
        Reasons = ["pure rename `Foo`→`Bar`"],
        ChangedLines = 9,
        GroupId = group,
    };

    [Fact]
    public void HunkTriageRoundTripsThroughJson()
    {
        var original = Hunk("src/A.cs", ChangeClass.Rename, TriageTier.Skip, "g1");

        var json = JsonSerializer.Serialize(original);
        var back = JsonSerializer.Deserialize<HunkTriage>(json)!;

        Assert.Equal(original.Path, back.Path);
        Assert.Equal(original.OldStart, back.OldStart);
        Assert.Equal(original.OldEnd, back.OldEnd);
        Assert.Equal(original.NewStart, back.NewStart);
        Assert.Equal(original.NewEnd, back.NewEnd);
        Assert.Equal(original.Class, back.Class);
        Assert.Equal(original.Tier, back.Tier);
        Assert.Equal(original.Reasons, back.Reasons);
        Assert.Equal(9, back.ChangedLines);
        Assert.Equal("g1", back.GroupId);
    }

    [Fact]
    public void EnumsSerialiseAsNames()
    {
        var json = JsonSerializer.Serialize(Hunk("a.cs", ChangeClass.BehaviourChange, TriageTier.WorthALook));

        Assert.Contains("\"BehaviourChange\"", json);
        Assert.Contains("\"WorthALook\"", json);
    }

    [Fact]
    public void AdditionsAndDeletionsKeepNullSpans()
    {
        var added = new HunkTriage
        {
            Path = "New.cs",
            NewStart = 1,
            NewEnd = 30,
            Class = ChangeClass.NewCode,
            Tier = TriageTier.Skim,
            Reasons = ["new file"],
        };

        var back = JsonSerializer.Deserialize<HunkTriage>(JsonSerializer.Serialize(added))!;

        Assert.Null(back.OldStart);
        Assert.Null(back.OldEnd);
        Assert.Null(back.GroupId);
        Assert.Equal(1, back.NewStart);
        Assert.Equal(30, back.NewEnd);
    }

    [Fact]
    public void TriageGroupRoundTripsWithMembers()
    {
        var group = new TriageGroup
        {
            Id = "g1",
            Class = ChangeClass.Rename,
            Title = "Renamed `Foo`→`Bar`",
            Members =
            [
                Hunk("src/A.cs", ChangeClass.Rename, TriageTier.Skip, "g1"),
                Hunk("src/B.cs", ChangeClass.Rename, TriageTier.Skip, "g1"),
            ],
        };

        var back = JsonSerializer.Deserialize<TriageGroup>(JsonSerializer.Serialize(group))!;

        Assert.Equal("g1", back.Id);
        Assert.Equal(ChangeClass.Rename, back.Class);
        Assert.Equal("Renamed `Foo`→`Bar`", back.Title);
        Assert.Equal(["src/A.cs", "src/B.cs"], back.Members.Select(m => m.Path));
        Assert.All(back.Members, m => Assert.Equal("g1", m.GroupId));
    }
}
