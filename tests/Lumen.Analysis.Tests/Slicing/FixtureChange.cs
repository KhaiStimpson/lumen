using Lumen.Domain;
using Lumen.Roslyn.Flags;
using Lumen.Roslyn.Slicing;
using Lumen.Roslyn.Triage;
using Lumen.TestSupport;

namespace Lumen.Analysis.Tests.Slicing;

/// <summary>
/// The Razor Pages fixture's change, analysed the way the engine does: git diff of base..head, triage over both sides,
/// red flags, the head's link graph and the slicer. Built once per test run (git and parsing take a couple of seconds).
/// </summary>
internal sealed class FixtureChange
{
    private static readonly Lazy<FixtureChange> Instance = new(Build);

    private FixtureChange(PullRequestSnapshot snapshot, TriageResult triage, LinkGraph graph, List<ChangedFileFacts> facts, TimeSpan triageTime)
    {
        Snapshot = snapshot;
        Triage = triage;
        Graph = graph;
        Facts = facts;
        TriageTime = triageTime;
    }

    public static FixtureChange Value => Instance.Value;

    public PullRequestSnapshot Snapshot { get; }

    public TriageResult Triage { get; }

    public LinkGraph Graph { get; }

    public List<ChangedFileFacts> Facts { get; }

    public TimeSpan TriageTime { get; }

    public SliceResult Slice(SlicerOptions? options = null) => Slicer.Slice(Graph, Facts, options);

    private static FixtureChange Build()
    {
        using var repo = FixtureRepository.Create();
        var diff = repo.Git("diff", "--no-color", "--no-ext-diff", "-M", "-U3", repo.BaseSha, repo.HeadSha, "--");
        var files = ChangedFiles.FromUnifiedDiff(diff);
        var snapshot = new PullRequestSnapshot(
            new PullRequestKey(new RepositoryRef("contoso", "shop"), 1),
            repo.BaseSha,
            repo.HeadSha,
            repo.BaseSha,
            new PullRequestMetadata("AI change", "agent", "open", false, "main", FixtureRepository.HeadBranch, "", null, DateTimeOffset.UnixEpoch),
            files,
            []);

        var baseDir = Path.Combine(FixtureRepository.FixtureDirectory, "base");
        var headDir = Path.Combine(FixtureRepository.FixtureDirectory, "head");
        string? Read(string root, string path) => File.Exists(Path.Combine(root, path)) ? File.ReadAllText(Path.Combine(root, path)) : null;
        var baseTexts = files.Where(f => Read(baseDir, f.OldPath ?? f.Path) is not null).ToDictionary(f => f.Path, f => Read(baseDir, f.OldPath ?? f.Path)!, StringComparer.Ordinal);
        var headTexts = files.Where(f => Read(headDir, f.Path) is not null).ToDictionary(f => f.Path, f => Read(headDir, f.Path)!, StringComparer.Ordinal);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // As the engine's TriageRunner does it: red flags from the same sources, and no fold hides one.
        var sources = new TriageSources(baseTexts, headTexts);
        var triage = RedFlags.OpenFlaggedFolds(RoslynTriage.CreatePipeline().Run(snapshot, sources), RedFlags.Detect(snapshot, sources, new ReviewSettings()));
        var triageTime = stopwatch.Elapsed;

        var graph = LinkResolver.Build(FixtureSources.Index("head"));
        return new FixtureChange(snapshot, triage, graph, Slicer.Facts(snapshot, triage), triageTime);
    }
}
