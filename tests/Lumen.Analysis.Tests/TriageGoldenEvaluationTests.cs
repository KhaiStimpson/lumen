using Lumen.Analysis.Tests.Support;
using Xunit.Abstractions;

namespace Lumen.Analysis.Tests;

/// <summary>
/// Scores the triage against the golden labels and prints the result. Opt-in (LUMEN_TRIAGE_EVAL=1) because it
/// is a measurement to read, not a pass/fail gate. Replays recorded diffs, so it needs no network or checkout.
/// Run: <c>dotnet test tests/Lumen.Analysis.Tests --filter TriageGoldenEvaluationTests --logger "console;verbosity=detailed"</c>
/// </summary>
public sealed class TriageGoldenEvaluationTests(ITestOutputHelper output)
{
    [Fact]
    public void PrintsEvaluationForAndrewCrm58()
    {
        if (Environment.GetEnvironmentVariable("LUMEN_TRIAGE_EVAL") != "1")
        {
            return;
        }

        var labels = TriageLabelSet.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "triage", "KhaiStimpson-andrew-crm-58", "labels.json"));
        var files = DiffsJsonl.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "andrew-crm-58", "diffs.jsonl"));

        var triage = TriagePipeline.Default.Run(TestDiffs.Snapshot(files));
        var report = TriageEvaluation.Evaluate(labels, triage);

        output.WriteLine($"{labels.Pr}: {files.Count} files, {triage.Hunks.Count} hunks, {labels.Labels.Count} labels");
        output.WriteLine(report.Describe());
    }

    [Fact]
    public void DiffsJsonlReplayParsesTheRecordedPullRequest()
    {
        var files = DiffsJsonl.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "andrew-crm-58", "diffs.jsonl"));

        Assert.Equal(46, files.Count);
        Assert.Contains(files, f => f.Path.EndsWith("CatchMeUpService.cs", StringComparison.Ordinal) && f.Deletions == 12);
        Assert.Contains(files, f => f.Path.EndsWith("AbrAbnProvider.cs", StringComparison.Ordinal) && f.Kind == Lumen.Domain.FileChangeKind.Added);
    }

    [Fact]
    public void DraftLabelsForAndrewCrm58AreWellFormedAndMarkedDraft()
    {
        var labels = TriageLabelSet.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "triage", "KhaiStimpson-andrew-crm-58", "labels.json"));
        var files = DiffsJsonl.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "andrew-crm-58", "diffs.jsonl"));

        Assert.Equal("KhaiStimpson/andrew-crm#58", labels.Pr);
        Assert.NotEmpty(labels.Labels);
        Assert.All(labels.Labels, l =>
        {
            Assert.True(l.Draft, $"{l.Path} is not marked draft");
            Assert.True(l.StartLine <= l.EndLine);
            Assert.Contains(files, f => f.Path == l.Path);
        });
        Assert.Contains(labels.Labels, l => l.Tier == Lumen.Domain.TriageTier.Critical);
    }
}
