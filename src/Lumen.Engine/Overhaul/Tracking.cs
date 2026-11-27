using System.Text.Json;
using Lumen.Analysis;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Engine.Mapping;
using Lumen.Engine.Sessions;

namespace Lumen.Engine.Overhaul;

/// <summary>
/// Reviewed marks, GitHub "Viewed" sync and since-my-last-look (docs/overhaul.md, Tracking).
/// <list type="bullet">
/// <item>A mark stores the unit's fingerprint when marked; a slice is reviewed when every unit in it is marked at its
/// current fingerprint, so a push that changes one file clears only that file's marks.</item>
/// <item>On a pull request, a file whose every slice is reviewed gets GitHub's Viewed box ticked.</item>
/// <item>Since my last look compares each file's patch against its own merge-base (merge-base..old vs
/// merge-base'..new), so a rebase onto a newer base doesn't flood the view.</item>
/// </list>
/// </summary>
public sealed partial class Tracking(
    IReviewMarkStore marks,
    IReviewStore cache,
    IGitHubClient gitHub,
    ReviewSettingsStore reviewSettings,
    TimeProvider time,
    ILogger<Tracking> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<MarksUpdated> SetMarkedAsync(PullRequestSession session, IReadOnlyList<string> units, bool marked, CancellationToken ct)
    {
        if (units.Count == 0)
        {
            throw new ReviewActionException(ReviewActionError.Invalid, "Name at least one unit to mark.");
        }

        foreach (var unit in units)
        {
            if (!marked)
            {
                await marks.RemoveMarkAsync(session.Target, unit, ct).ConfigureAwait(false);
                continue;
            }

            if (!session.Units.TryGetValue(unit, out var fingerprint))
            {
                throw new ReviewActionException(ReviewActionError.NotFound, $"'{unit}' isn't part of this change.");
            }

            await marks.SetMarkAsync(session.Target, new Domain.ReviewMark(unit, fingerprint.Fingerprint, time.GetUtcNow()), ct).ConfigureAwait(false);
        }

        var stored = await marks.GetMarksAsync(session.Target, ct).ConfigureAwait(false);
        var reply = new MarksUpdated();
        reply.Marks.AddRange(stored.Select(m => new Contracts.ReviewMark { Unit = m.Unit, Fingerprint = m.Fingerprint }));
        if (marked && session.Target.CanPost)
        {
            reply.ViewedPaths.AddRange(await SyncViewedAsync(session, stored, units, ct).ConfigureAwait(false));
        }

        session.Publish(new PullRequestEvent { MarksUpdated = reply.Clone() });
        return reply;
    }

    /// <summary>Which files the change in <paramref name="changedUnits"/> completed: every slice holding them is now reviewed.</summary>
    public static IReadOnlyList<string> FilesNowReviewed(
        SliceResult slices,
        IReadOnlyDictionary<string, Domain.UnitFingerprint> units,
        IReadOnlyList<Domain.ReviewMark> marks,
        IEnumerable<string> changedUnits)
    {
        var marked = marks.ToDictionary(m => m.Unit, m => m.Fingerprint, StringComparer.Ordinal);
        bool UnitReviewed(string unit) => units.TryGetValue(unit, out var fp) && marked.TryGetValue(unit, out var at) && at == fp.Fingerprint;
        IEnumerable<string> UnitsOfFile(string path) => units.Values.Where(u => u.Path == path).Select(u => u.Unit);
        bool SliceReviewed(Domain.Slice slice) => slice.Files.SelectMany(f => UnitsOfFile(f.Path)).All(UnitReviewed);

        var paths = changedUnits.Select(u => units.TryGetValue(u, out var fp) ? fp.Path : u.Split("::", 2)[0]).Distinct(StringComparer.Ordinal);
        return [.. paths.Where(path =>
        {
            var holding = slices.Slices.Where(s => s.Files.Any(f => f.Path == path)).ToList();
            return holding.Count > 0 && holding.All(SliceReviewed);
        })];
    }

    private async Task<List<string>> SyncViewedAsync(PullRequestSession session, IReadOnlyList<Domain.ReviewMark> stored, IReadOnlyList<string> changed, CancellationToken ct)
    {
        var ticked = new List<string>();
        if (session.Slices is not { FellBack: false } slices)
        {
            return ticked;
        }

        foreach (var path in FilesNowReviewed(slices, session.Units, stored, changed))
        {
            try
            {
                await gitHub.SetFileViewedAsync(session.Key, path, true, ct).ConfigureAwait(false);
                session.Publish(new PullRequestEvent { FileViewed = new FileViewed { Path = path, Viewed = true } });
                ticked.Add(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The mark is stored either way; the Viewed tick is a courtesy to github.com.
                LogViewedNotSynced(logger, ex, session.Key, path);
            }
        }

        return ticked;
    }

    // ---------------------------------------------------------------- since my last look

    public static string FingerprintKey(ReviewTarget target, string head) => $"units:{target.Key}:{head}";

    public static string FlagsKey(ReviewTarget target, string head) => $"flags:{target.Key}:{head}";

    /// <summary>Remembers this head's unit fingerprints and flags, for the next look's fallback and Fixed / New flags.</summary>
    public async Task RememberAsync(PullRequestSession session, string head, CancellationToken ct)
    {
        try
        {
            await cache.SetCacheAsync(FingerprintKey(session.Target, head), JsonSerializer.Serialize(session.Units.Values.ToList(), Json), ct).ConfigureAwait(false);
            await cache.SetCacheAsync(FlagsKey(session.Target, head), JsonSerializer.Serialize(session.Flags.ToList(), Json), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNotRemembered(logger, ex, session.Key);
        }
    }

    public async Task<SinceLastLook> SinceLastLookAsync(PullRequestSession session, CancellationToken ct)
    {
        var snapshot = session.Snapshot ?? throw new ReviewActionException(ReviewActionError.Loading, "The change is still loading.");
        var checkout = session.Checkout ?? throw new ReviewActionException(ReviewActionError.Loading, "The change is still loading.");
        var reply = new SinceLastLook { NewHeadSha = snapshot.HeadSha };
        if (session.PreviousVisit?.LastOpenedHead is not { Length: > 0 } old || old == snapshot.HeadSha)
        {
            reply.OldHeadSha = session.PreviousVisit?.LastOpenedHead ?? "";
            reply.Note = reply.OldHeadSha.Length == 0 ? "You haven't looked at this change before." : "Nothing changed since your last look.";
            return reply;
        }

        reply.OldHeadSha = old;
        var settings = reviewSettings.Resolve(session.Key.Repository);
        if (await checkout.EnsureCommitAsync(old, ct).ConfigureAwait(false))
        {
            foreach (var file in await ChangedSinceAsync(checkout, snapshot, old, settings, ct).ConfigureAwait(false))
            {
                reply.Files.Add(ProtoMapper.ToProto(file));
            }
        }
        else
        {
            // The old head is gone (history rewritten): compare each unit's changed lines with what was remembered.
            reply.HistoryRewritten = true;
            reply.Note = $"The head you last looked at ({Short(old)}) no longer exists: history was rewritten, so files were compared by their changed lines.";
            var before = await LoadAsync<List<Domain.UnitFingerprint>>(FingerprintKey(session.Target, old), ct).ConfigureAwait(false) ?? [];
            var was = before.ToDictionary(u => u.Unit, u => u.Fingerprint, StringComparer.Ordinal);
            var changedPaths = session.Units.Values.Where(u => !was.TryGetValue(u.Unit, out var fp) || fp != u.Fingerprint).Select(u => u.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var file in snapshot.Files.Where(f => changedPaths.Contains(f.Path)))
            {
                reply.Files.Add(ProtoMapper.ToProto(file));
            }
        }

        var oldFlags = await LoadAsync<List<Domain.RedFlag>>(FlagsKey(session.Target, old), ct).ConfigureAwait(false) ?? [];
        var oldIds = oldFlags.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var newIds = session.Flags.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        reply.NewFlagIds.AddRange(session.Flags.Where(f => !oldIds.Contains(f.Id)).Select(f => f.Id));
        reply.FixedFlags.AddRange(oldFlags.Where(f => !newIds.Contains(f.Id)).Select(ProtoMapper.ToProto));
        return reply;
    }

    /// <summary>
    /// Files whose patch changed: merge-base(old, base)..old against merge-base..new, compared on their normalised changed
    /// lines. Each is reduced to the diff from the old head.
    /// </summary>
    public static async Task<IReadOnlyList<ChangedFile>> ChangedSinceAsync(
        IPullRequestCheckout checkout,
        Domain.PullRequestSnapshot snapshot,
        string old,
        ReviewSettings settings,
        CancellationToken ct)
    {
        var oldMergeBase = await MergeBaseAsync(checkout, old, snapshot, ct).ConfigureAwait(false);
        var oldFiles = ChangedFiles.FromUnifiedDiff(await checkout.GetRangeDiffAsync(oldMergeBase, old, false, null, ct).ConfigureAwait(false), settings.MechanicalPaths)
            .ToDictionary(f => f.Path, StringComparer.Ordinal);
        var newFiles = snapshot.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var changed = oldFiles.Keys.Union(newFiles.Keys, StringComparer.Ordinal)
            .Where(path => Patch(oldFiles.GetValueOrDefault(path)) != Patch(newFiles.GetValueOrDefault(path)))
            .ToList();
        if (changed.Count == 0)
        {
            return [];
        }

        var range = ChangedFiles.FromUnifiedDiff(await checkout.GetRangeDiffAsync(old, snapshot.HeadSha, false, null, ct).ConfigureAwait(false), settings.MechanicalPaths)
            .ToDictionary(f => f.Path, StringComparer.Ordinal);
        return [.. changed.Select(path => range.GetValueOrDefault(path) ?? newFiles.GetValueOrDefault(path) ?? oldFiles[path])];
    }

    /// <summary>A file's patch as its changed lines, without line numbers or context: equal patches mean nothing to re-read.</summary>
    public static string Patch(ChangedFile? file) =>
        file is null
            ? ""
            : string.Join('\n', file.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind != Domain.DiffLineKind.Context)
                .Select(l => (l.Kind == Domain.DiffLineKind.Added ? "+" : "-") + Domain.RedFlag.Normalise(l.Text)));

    private static async Task<string> MergeBaseAsync(IPullRequestCheckout checkout, string old, Domain.PullRequestSnapshot snapshot, CancellationToken ct)
    {
        // The old head's own fork point: if the base moved on since, the newer base still descends from the old one.
        foreach (var candidate in new[] { snapshot.MergeBaseSha, snapshot.BaseSha })
        {
            if (await checkout.IsAncestorAsync(candidate, old, ct).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        // A rebase onto a newer base: walk the old head's first parents back until one is an ancestor of the base.
        return await checkout.MergeBaseOfAsync(old, snapshot.BaseSha, ct).ConfigureAwait(false) ?? snapshot.MergeBaseSha;
    }

    private async Task<T?> LoadAsync<T>(string key, CancellationToken ct)
        where T : class
    {
        var json = await cache.GetCacheAsync(key, ct).ConfigureAwait(false);
        return json is null ? null : JsonSerializer.Deserialize<T>(json, Json);
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    [LoggerMessage(Level = LogLevel.Warning, Message = "{PullRequest}: couldn't tick Viewed on {Path}")]
    private static partial void LogViewedNotSynced(ILogger logger, Exception exception, PullRequestKey pullRequest, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{PullRequest}: couldn't remember this head's fingerprints and flags")]
    private static partial void LogNotRemembered(ILogger logger, Exception exception, PullRequestKey pullRequest);
}
