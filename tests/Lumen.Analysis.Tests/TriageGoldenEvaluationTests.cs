using System.Diagnostics;
using Lumen.Analysis.Tests.Support;
using Lumen.Domain;
using Lumen.Roslyn.Triage;
using Xunit.Abstractions;

namespace Lumen.Analysis.Tests;

/// <summary>
/// Scores the triage against the golden labels and prints the result. Opt-in (LUMEN_TRIAGE_EVAL=1) because it
/// is a measurement to read, not a pass/fail gate. Replays recorded diffs, so it needs no network. With
/// LUMEN_TRIAGE_REPO pointing at a local clone that has the PR's commits, file sources come from <c>git show</c> and
/// the Roslyn proofs can run; without it only added files and file-level rules are judged.
/// Run: <c>dotnet test tests/Lumen.Analysis.Tests --filter TriageGoldenEvaluationTests --logger "console;verbosity=detailed"</c>
/// </summary>
public sealed class TriageGoldenEvaluationTests(ITestOutputHelper output)
{
    private const string MergeBase = "ecf9d53ed2c3b9482a094681cdf501cf2c8c7a0c";
    private const string Head = "2baaca76d26ccd0b73159e1e9c11a97dc73c360d";

    private static string LabelsPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "triage", "KhaiStimpson-andrew-crm-58", "labels.json");

    private static string DiffsPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "andrew-crm-58", "diffs.jsonl");

    [Fact]
    public async Task PrintsEvaluationForAndrewCrm58()
    {
        if (Environment.GetEnvironmentVariable("LUMEN_TRIAGE_EVAL") != "1")
        {
            return;
        }

        var labels = TriageLabelSet.Load(LabelsPath);
        var files = DiffsJsonl.Load(DiffsPath);
        var snapshot = TestDiffs.Snapshot(files);
        var repo = Environment.GetEnvironmentVariable("LUMEN_TRIAGE_REPO");
        var sources = string.IsNullOrEmpty(repo) ? TriageSources.None : await LoadFromGitAsync(repo, files);

        var sw = Stopwatch.StartNew();
        var triage = RoslynTriage.CreatePipeline().Run(snapshot, sources);
        var report = TriageEvaluation.Evaluate(labels, triage);

        output.WriteLine($"{labels.Pr}: {files.Count} files, {triage.Hunks.Count} hunk parts, {triage.Groups.Count} groups, {labels.Labels.Count} labels; sources: {(sources == TriageSources.None ? "none" : sources.Paths.Count() + " files")}; triaged in {sw.ElapsedMilliseconds} ms");
        output.WriteLine(report.Describe());
        foreach (var group in triage.Groups)
        {
            output.WriteLine($"  group {group.Id}: {group.Title} ({group.Members.Count} hunks)");
        }

        foreach (var hunk in triage.Hunks.Where(h => h.Tier is TriageTier.Skip or TriageTier.Critical && h.Class != ChangeClass.Generated))
        {
            output.WriteLine($"  {hunk.Tier} {hunk.Class} {hunk.Path}:{hunk.NewStart ?? hunk.OldStart} — {string.Join("; ", hunk.Reasons)}");
        }
    }

    [Fact]
    public void DiffsJsonlReplayParsesTheRecordedPullRequest()
    {
        var files = DiffsJsonl.Load(DiffsPath);

        Assert.Equal(46, files.Count);
        Assert.Contains(files, f => f.Path.EndsWith("CatchMeUpService.cs", StringComparison.Ordinal) && f.Deletions == 12);
        Assert.Contains(files, f => f.Path.EndsWith("AbrAbnProvider.cs", StringComparison.Ordinal) && f.Kind == FileChangeKind.Added);
    }

    [Fact]
    public void DraftLabelsForAndrewCrm58AreWellFormedAndMarkedDraft()
    {
        var labels = TriageLabelSet.Load(LabelsPath);
        var files = DiffsJsonl.Load(DiffsPath);

        Assert.Equal("KhaiStimpson/andrew-crm#58", labels.Pr);
        Assert.NotEmpty(labels.Labels);
        Assert.All(labels.Labels, l =>
        {
            Assert.True(l.Draft, $"{l.Path} is not marked draft");
            Assert.True(l.StartLine <= l.EndLine);
            Assert.Contains(files, f => f.Path == l.Path);
        });
        Assert.Contains(labels.Labels, l => l.Tier == TriageTier.Critical);
    }

    [Fact]
    public void ReplayWithoutSourcesCallsNothingMechanical()
    {
        var labels = TriageLabelSet.Load(LabelsPath);
        var triage = RoslynTriage.CreatePipeline().Run(TestDiffs.Snapshot(DiffsJsonl.Load(DiffsPath)));

        Assert.Empty(TriageEvaluation.Evaluate(labels, triage).MechanicalFalsePositives);
    }

    private static async Task<TriageSources> LoadFromGitAsync(string repo, IReadOnlyList<ChangedFile> files)
    {
        var baseTexts = new Dictionary<string, string>(StringComparer.Ordinal);
        var headTexts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file.Kind != FileChangeKind.Added && await ShowAsync(repo, MergeBase, file.Path) is { } b)
            {
                baseTexts[file.Path] = b;
            }

            if (await ShowAsync(repo, Head, file.Path) is { } h)
            {
                headTexts[file.Path] = h;
            }
        }

        return new TriageSources(baseTexts, headTexts);
    }

    private static async Task<string?> ShowAsync(string repo, string sha, string path)
    {
        var psi = new ProcessStartInfo("git", ["-C", repo, "show", $"{sha}:{path}"]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(psi)!;
        var text = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode == 0 ? text : null;
    }
}
