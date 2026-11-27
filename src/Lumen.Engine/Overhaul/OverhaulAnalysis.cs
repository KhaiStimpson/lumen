using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumen.Analysis;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Engine.Mapping;
using Lumen.Engine.Sessions;
using Lumen.Roslyn.Slicing;

namespace Lumen.Engine.Overhaul;

/// <summary>Limits for the overhaul's analysis. The slicing budget is measured on the fixture (docs/overhaul.md, Phase 4).</summary>
public sealed record OverhaulOptions
{
    /// <summary>
    /// A safety net, not a target: past this, slicing stops and the review stays on the file list with the mechanical
    /// folds. Slicing runs after the diff, folds and red flags are published, so a long one never blocks reading.
    /// </summary>
    public TimeSpan SliceBudget { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>A file with more changed lines than this, and at least two changed members, is marked per member.</summary>
    public int MemberUnitLines { get; init; } = 150;

    public SlicerOptions Slicer { get; init; } = new();
}

/// <summary>
/// The overhaul's analysis after triage (docs/overhaul.md): feature slices, red flags and reviewed marks. Every part
/// is best effort: a failure is logged and the review goes on as today's file list with the mechanical folds.
/// </summary>
public sealed partial class OverhaulAnalysis(
    IReviewMarkStore marks,
    IReviewStore cache,
    Tracking tracking,
    ReviewProgressReader progress,
    OverhaulOptions options,
    ILogger<OverhaulAnalysis> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ParsedFileCache Parsed = new();

    public async Task RunAsync(
        PullRequestSession session,
        Domain.PullRequestSnapshot snapshot,
        IPullRequestCheckout checkout,
        ReviewSettings settings,
        TriageResult? triage,
        CancellationToken cancellationToken)
    {
        // Flags come from triage and marks from storage: neither waits on slicing, which can take minutes on a big change.
        session.Flags = triage?.Flags ?? [];
        var flags = new FlagsReady();
        flags.Flags.AddRange(session.Flags.Select(ProtoMapper.ToProto));
        session.Publish(new PullRequestEvent { FlagsReady = flags });
        await PublishMarksAsync(session, cancellationToken).ConfigureAwait(false);

        var slices = await SliceAsync(session, snapshot, checkout, triage, cancellationToken).ConfigureAwait(false);
        session.Slices = slices.FellBack ? null : slices;
        session.Units = Units(snapshot, triage, slices.FellBack ? null : session.Index, options.MemberUnitLines);
        session.Publish(new PullRequestEvent { SlicesReady = ToProto(slices, session.Units.Values) });
        await tracking.RememberAsync(session, snapshot.HeadSha, cancellationToken).ConfigureAwait(false);
        await SaveSummaryAsync(session, snapshot, triage, cancellationToken).ConfigureAwait(false);
    }

    public static string CacheKey(ReviewTarget target, string head, string mergeBase) => $"slices:{Slicer.Version}:{target.Key}:{head}:{mergeBase}";

    private async Task<SliceResult> SliceAsync(PullRequestSession session, Domain.PullRequestSnapshot snapshot, IPullRequestCheckout checkout, TriageResult? triage, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var step = Stopwatch.StartNew();
        var stage = "reading the repository";
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.SliceBudget);
        try
        {
            // Each step says how much it has to do and how long the last one took, so a slow repository shows where
            // the time goes in the app itself (no log needed).
            SliceProgress(session, "Building feature slices: reading the repository…");
            var index = await BuildIndexAsync(checkout, budget.Token).ConfigureAwait(false);
            var indexed = index.CSharp.Count + index.Razor.Count + index.Scripts.Count + index.WebForms.Count;
            session.Index = index;
            var key = CacheKey(session.Target, snapshot.HeadSha, snapshot.MergeBaseSha);
            if (await cache.GetCacheAsync(key, ct).ConfigureAwait(false) is { } cached && JsonSerializer.Deserialize<SliceResult>(cached, Json) is { } hit)
            {
                return hit;
            }

            stage = "linking files";
            SliceProgress(session, string.Create(CultureInfo.InvariantCulture, $"Building feature slices: linking {indexed:N0} files (read in {Describe(step.Elapsed)})…"));
            step.Restart();
            var graph = await Task.Run(() => LinkResolver.Build(index), budget.Token).ConfigureAwait(false);
            stage = "grouping changes";
            SliceProgress(session, string.Create(CultureInfo.InvariantCulture, $"Building feature slices: grouping {snapshot.Files.Count:N0} changed files (linked in {Describe(step.Elapsed)})…"));
            step.Restart();
            var result = await Task.Run(() => Slicer.Slice(graph, Slicer.Facts(snapshot, triage), options.Slicer, budget.Token), budget.Token).ConfigureAwait(false);
            result = result with { Duration = stopwatch.Elapsed };
            await cache.SetCacheAsync(key, JsonSerializer.Serialize(result, Json), ct).ConfigureAwait(false);
            LogSliced(logger, session.Key, result.Slices.Count, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogOverBudget(logger, session.Key, (long)options.SliceBudget.TotalMilliseconds);
            return SliceResult.Fallback($"Slicing took longer than {Describe(options.SliceBudget)} (it stopped while {stage}), so the change is shown as a file list.", stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSliceFailed(logger, ex, session.Key);
            return SliceResult.Fallback($"Slicing failed while {stage} ({ex.GetType().Name}), so the change is shown as a file list.", stopwatch.Elapsed);
        }
    }

    /// <summary>The stage the app watches to show slicing is under way; it stops when SlicesReady arrives.</summary>
    public const string SlicesStage = "slices";

    private static void SliceProgress(PullRequestSession session, string message) =>
        session.Publish(new PullRequestEvent { Progress = new AnalysisProgress { Stage = SlicesStage, Message = message } });

    private static string Describe(TimeSpan span) => span.TotalMinutes >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{span.TotalMinutes:0.#} min")
        : string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0.#} s");

    /// <summary>Every file at head, reading the ones the link resolver understands (code, views, scripts, WebForms).</summary>
    public static async Task<RepositoryIndex> BuildIndexAsync(IPullRequestCheckout checkout, CancellationToken ct)
    {
        var paths = await checkout.ListHeadFilesAsync(ct).ConfigureAwait(false);
        var indexed = paths.Where(RepositoryIndex.IsIndexed).ToList();
        var texts = await checkout.ReadHeadFilesAsync(indexed, ct).ConfigureAwait(false);
        var files = paths.Select(p => new KeyValuePair<string, string>(p, texts.GetValueOrDefault(p) ?? ""));
        return RepositoryIndex.Build(files, Parsed);
    }

    // ---------------------------------------------------------------- units

    /// <summary>
    /// The fingerprint of each unit's normalised changed lines: a file, or each changed member of a large file (plus the
    /// file for any lines outside members). Line numbers and context aren't part of it.
    /// </summary>
    public static Dictionary<string, Domain.UnitFingerprint> Units(Domain.PullRequestSnapshot snapshot, TriageResult? triage, RepositoryIndex? index, int memberUnitLines)
    {
        var units = new Dictionary<string, Domain.UnitFingerprint>(StringComparer.Ordinal);
        var folds = (triage?.Hunks ?? []).ToLookup(h => h.Path, StringComparer.Ordinal);
        foreach (var file in snapshot.Files)
        {
            var lines = ChangedLines(file).ToList();
            var openSpans = folds[file.Path].Where(h => h.Fold == Domain.FoldKind.Open).ToList();
            bool IsOpen((Domain.DiffLine Line, int At) x) => !folds[file.Path].Any()
                ? !file.Mechanical.IsMechanical
                : openSpans.Any(h => x.Line.Kind == Domain.DiffLineKind.Added
                    ? h.NewStart <= x.Line.NewNumber && x.Line.NewNumber <= h.NewEnd
                    : h.OldStart <= x.Line.OldNumber && x.Line.OldNumber <= h.OldEnd);

            var members = index is not null && file.Additions + file.Deletions > memberUnitLines && index.CSharp.TryGetValue(file.Path, out var cs)
                ? cs.Types.SelectMany(t => t.Members.Where(m => m.Kind != "field").Select(m => (Unit: $"{file.Path}::{t.Name}.{(m.Kind == "constructor" ? t.SimpleName : m.Name)}", m.StartLine, m.EndLine))).ToList()
                : [];
            var byMember = members.Count == 0 ? [] : lines.GroupBy(x => members.FirstOrDefault(m => x.At >= m.StartLine && x.At <= m.EndLine).Unit ?? file.Path).ToList();
            if (byMember.Count(g => g.Key != file.Path) < 2)
            {
                units[file.Path] = new Domain.UnitFingerprint(file.Path, file.Path, Fingerprint(lines.Select(x => x.Line)), lines.Count(IsOpen));
                continue;
            }

            foreach (var group in byMember)
            {
                units[group.Key] = new Domain.UnitFingerprint(group.Key, file.Path, Fingerprint(group.Select(x => x.Line)), group.Count(IsOpen));
            }
        }

        return units;
    }

    /// <summary>Changed lines with the head line they sit at (a removed line takes the next head line).</summary>
    private static IEnumerable<(Domain.DiffLine Line, int At)> ChangedLines(ChangedFile file)
    {
        foreach (var hunk in file.Hunks)
        {
            var pending = new List<Domain.DiffLine>();
            foreach (var line in hunk.Lines)
            {
                if (line.Kind == Domain.DiffLineKind.Removed)
                {
                    pending.Add(line);
                    continue;
                }

                var at = line.NewNumber ?? 0;
                foreach (var removed in pending)
                {
                    yield return (removed, at);
                }

                pending.Clear();
                if (line.Kind == Domain.DiffLineKind.Added)
                {
                    yield return (line, at);
                }
            }

            foreach (var removed in pending)
            {
                yield return (removed, hunk.NewStart + hunk.NewCount);
            }
        }
    }

    public static string Fingerprint(IEnumerable<Domain.DiffLine> lines)
    {
        var text = string.Join('\n', lines.Select(l => (l.Kind == Domain.DiffLineKind.Added ? "+" : "-") + Domain.RedFlag.Normalise(l.Text)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    // ---------------------------------------------------------------- publishing

    public static SlicesReady ToProto(SliceResult result, IEnumerable<Domain.UnitFingerprint> units)
    {
        var ready = new SlicesReady
        {
            FellBack = result.FellBack,
            FallbackReason = result.FallbackReason ?? "",
            DurationMs = (long)result.Duration.TotalMilliseconds,
        };
        ready.Slices.AddRange(result.Slices.Select(s =>
        {
            var slice = new Contracts.Slice
            {
                Id = s.Id,
                Title = s.Title,
                Kind = (Contracts.SliceKind)s.Kind,
                SeedPath = s.SeedPath,
            };
            slice.Sections.AddRange(s.Sections);
            slice.Files.AddRange(s.Files.Select(f =>
            {
                var file = new Contracts.SliceFile { Path = f.Path, Order = f.Order, Why = f.Why, Section = f.Section, OutsideCallers = f.OutsideCallers };
                file.Members.AddRange(f.Members);
                return file;
            }));
            slice.PossibleLinks.AddRange(s.PossibleLinks.Select(Edge));
            slice.PageLinks.AddRange(s.PageLinks.Select(Edge));
            return slice;
        }));
        ready.Edges.AddRange(result.Edges.Select(Edge));
        ready.Units.AddRange(units.Select(u => new Contracts.UnitFingerprint { Unit = u.Unit, Path = u.Path, Fingerprint = u.Fingerprint, OpenLines = u.OpenLines }));
        return ready;
    }

    private static Contracts.SliceEdge Edge(Domain.SliceEdge e) => new() { From = e.From, To = e.To, Reason = e.Reason, Possible = e.Possible };

    /// <summary>The stored marks for this target; the app works out which still match their unit's fingerprint.</summary>
    internal async Task PublishMarksAsync(PullRequestSession session, CancellationToken cancellationToken)
    {
        try
        {
            var stored = await marks.GetMarksAsync(session.Target, cancellationToken).ConfigureAwait(false);
            var update = new MarksUpdated();
            update.Marks.AddRange(stored.Select(m => new Contracts.ReviewMark { Unit = m.Unit, Fingerprint = m.Fingerprint }));
            session.Publish(new PullRequestEvent { MarksUpdated = update });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogMarksUnavailable(logger, ex, session.Key);
        }
    }

    private async Task SaveSummaryAsync(PullRequestSession session, Domain.PullRequestSnapshot snapshot, TriageResult? triage, CancellationToken ct)
    {
        try
        {
            var changed = snapshot.Files.Sum(f => f.Additions + f.Deletions);
            var open = triage?.Hunks.Where(h => h.Fold == Domain.FoldKind.Open).Sum(h => h.ChangedLines) ?? changed;
            var sliceUnits = session.Slices?.Slices.Where(s => s.Kind != Domain.SliceKind.MechanicalOnly)
                .Select(s => (IReadOnlyList<string>)[.. s.Files.SelectMany(f => session.Units.Values.Where(u => u.Path == f.Path).Select(u => u.Unit))])
                .ToList() ?? [];
            var summary = new AnalysisSummary(snapshot.HeadSha, changed, open, sliceUnits, session.Units.ToDictionary(kv => kv.Key, kv => kv.Value.Fingerprint, StringComparer.Ordinal));
            await progress.SaveAsync(session.Target, summary, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogMarksUnavailable(logger, ex, session.Key);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{PullRequest}: couldn't read reviewed marks")]
    private static partial void LogMarksUnavailable(ILogger logger, Exception exception, PullRequestKey pullRequest);

    [LoggerMessage(Level = LogLevel.Information, Message = "{PullRequest}: {Slices} slices in {Elapsed} ms")]
    private static partial void LogSliced(ILogger logger, PullRequestKey pullRequest, int slices, long elapsed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{PullRequest}: slicing ran past its {Budget} ms budget; showing the file list")]
    private static partial void LogOverBudget(ILogger logger, PullRequestKey pullRequest, long budget);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{PullRequest}: slicing failed; showing the file list")]
    private static partial void LogSliceFailed(ILogger logger, Exception exception, PullRequestKey pullRequest);
}
