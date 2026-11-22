using Lumen.Domain;

namespace Lumen.Analysis.Tests;

public class TriageSummaryTextTests
{
    private static HunkTriage Hunk(ChangeClass cls, TriageTier tier, int lines) => new()
    {
        Path = "a.cs",
        NewStart = 1,
        NewEnd = 1,
        Class = cls,
        Tier = tier,
        Reasons = ["r"],
        ChangedLines = lines,
    };

    [Fact]
    public void DescribesTheSplitInWordsWithThousandsSeparators()
    {
        var triage = new TriageResult(
        [
            Hunk(ChangeClass.Rename, TriageTier.Skip, 9_000),
            Hunk(ChangeClass.Generated, TriageTier.Skip, 5_100),
            Hunk(ChangeClass.NewCode, TriageTier.Skim, 3_200),
            Hunk(ChangeClass.BehaviourChange, TriageTier.WorthALook, 2_000),
            Hunk(ChangeClass.BehaviourChange, TriageTier.Critical, 400),
        ], []);

        Assert.Equal(
            "19,700 lines changed: 14,100 proven mechanical, 3,200 new code, 2,400 changing existing behaviour",
            TriageSummaryText.Describe(triage));
    }

    [Fact]
    public void MechanicalLinesNotProvenAreCalledOutSeparately()
    {
        var triage = new TriageResult(
        [
            Hunk(ChangeClass.Ripple, TriageTier.Skim, 12),
            Hunk(ChangeClass.Formatting, TriageTier.Skip, 3),
        ], []);

        Assert.Equal("15 lines changed: 3 proven mechanical, 12 mechanical but worth a skim", TriageSummaryText.Describe(triage));
        Assert.Equal((3, 12), (TriageSummaryText.ProvenMechanical(triage), TriageSummaryText.LikelyMechanical(triage)));
    }

    [Fact]
    public void EmptyAndSingularCasesReadNaturally()
    {
        Assert.Equal("No lines changed", TriageSummaryText.Describe(TriageResult.Empty));
        Assert.Equal("1 line changed: 1 new code", TriageSummaryText.Describe(new TriageResult([Hunk(ChangeClass.NewCode, TriageTier.Skim, 1)], [])));
    }
}
