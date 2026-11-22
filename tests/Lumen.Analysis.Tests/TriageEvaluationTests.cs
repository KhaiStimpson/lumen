using Lumen.Analysis.Tests.Support;
using Lumen.Domain;

namespace Lumen.Analysis.Tests;

public class TriageEvaluationTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "triage", "acme-shop-1");

    /// <summary>Stands in for the Phase 2 classifiers: calls Fmt.cs and Sneaky.cs formatting-only. Sneaky.cs is a lie.</summary>
    private sealed class ClaimsFormatting : IHunkClassifier
    {
        public HunkVerdict? Classify(HunkContext context) =>
            context.File.Path is "src/Fmt.cs" or "src/Sneaky.cs"
                ? new(ChangeClass.Formatting, TriageTier.Skip, ["formatting only"])
                : null;
    }

    private static (TriageLabelSet Labels, TriageResult Triage) Load()
    {
        var labels = TriageLabelSet.Load(Path.Combine(FixtureDir, "labels.json"));
        var files = ChangedFiles.FromUnifiedDiff(File.ReadAllText(Path.Combine(FixtureDir, "diff.patch")));
        var triage = new TriagePipeline([new ClaimsFormatting()]).Run(TestDiffs.Snapshot(files));
        return (labels, triage);
    }

    [Fact]
    public void LabelsLoadFromTheFixtureFormat()
    {
        var labels = TriageLabelSet.Load(Path.Combine(FixtureDir, "labels.json")).Labels;

        Assert.Equal(5, labels.Count);
        var guard = labels[0];
        Assert.Equal(("src/Order.cs", "old", 13, TriageTier.Critical, "removed null guard"), (guard.Path, guard.Side, guard.StartLine, guard.Tier, guard.Note));
        Assert.True(labels.Single(l => l.Path == "src/Fresh.cs").Draft);
        Assert.False(guard.Draft);
        Assert.Equal("head", labels[1].Side);
    }

    [Fact]
    public void LabelSetRoundTripsThroughJson()
    {
        var set = TriageLabelSet.Load(Path.Combine(FixtureDir, "labels.json"));

        var back = TriageLabelSet.Parse(set.ToJson());

        Assert.Equal(set.Labels, back.Labels);
    }

    [Fact]
    public void ReportsCriticalRecall()
    {
        var (labels, triage) = Load();

        var report = TriageEvaluation.Evaluate(labels, triage);

        Assert.Equal(3, report.CriticalLabels);
        Assert.Equal(1, report.CriticalRecalled);
        Assert.Equal(1.0 / 3, report.CriticalRecall, 3);
        Assert.Equal(["src/Sneaky.cs", "src/Gone.cs"], report.MissedCritical.Select(l => l.Path));
    }

    [Fact]
    public void FlagsRealChangesCalledMechanical()
    {
        var (labels, triage) = Load();

        var report = TriageEvaluation.Evaluate(labels, triage);

        var fp = Assert.Single(report.MechanicalFalsePositives);
        Assert.Equal("src/Sneaky.cs", fp.Label.Path);
        Assert.Equal(ChangeClass.Formatting, fp.Hunk.Class);
    }

    [Fact]
    public void CorrectlySkippedFormattingIsNotAFalsePositive()
    {
        var (labels, triage) = Load();

        var report = TriageEvaluation.Evaluate(labels, triage);

        Assert.DoesNotContain(report.MechanicalFalsePositives, f => f.Label.Path == "src/Fmt.cs");
    }

    [Fact]
    public void ReportsLabelsThatMatchNoHunk()
    {
        var (labels, triage) = Load();

        var report = TriageEvaluation.Evaluate(labels, triage);

        Assert.Equal("src/Gone.cs", Assert.Single(report.UnmatchedLabels).Path);
    }

    [Fact]
    public void CountsChangedLinesPerTierAndClass()
    {
        var (labels, triage) = Load();

        var report = TriageEvaluation.Evaluate(labels, triage);

        Assert.Equal(1, report.LinesPerTier[TriageTier.WorthALook]);
        Assert.Equal(4, report.LinesPerTier[TriageTier.Skip]);
        Assert.Equal(3, report.LinesPerTier[TriageTier.Skim]);
        Assert.False(report.LinesPerTier.ContainsKey(TriageTier.Critical));
        Assert.Equal(4, report.LinesPerClass[ChangeClass.Formatting]);
        Assert.Equal(8, report.TotalLines);
        Assert.Equal(1, report.DraftLabels);
    }

    [Fact]
    public void DescribeNamesTheFindingsInWords()
    {
        var (labels, triage) = Load();

        var text = TriageEvaluation.Evaluate(labels, triage).Describe();

        Assert.Contains("Critical recall: 1/3", text, StringComparison.Ordinal);
        Assert.Contains("Mechanical false positives: 1", text, StringComparison.Ordinal);
        Assert.Contains("src/Sneaky.cs:8-8", text, StringComparison.Ordinal);
        Assert.Contains("Draft labels", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCriticalLabelsMeansFullRecall()
    {
        var report = TriageEvaluation.Evaluate(new TriageLabelSet([]), TriageResult.Empty);

        Assert.Equal(1.0, report.CriticalRecall);
        Assert.Empty(report.MechanicalFalsePositives);
    }
}
