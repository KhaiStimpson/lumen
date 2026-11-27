using System.Text.Json;
using Lumen.Contracts;
using Lumen.Domain;

namespace Lumen.Engine.Overhaul;

/// <summary>What the last analysis of a target left behind, written after each analysis and read by the inbox.</summary>
public sealed record AnalysisSummary(string Head, int ChangedLines, int OpenLines, IReadOnlyList<IReadOnlyList<string>> SliceUnits, IReadOnlyDictionary<string, string> Fingerprints);

/// <summary>Turns a stored <see cref="AnalysisSummary"/> and the target's marks into the inbox's progress figures.</summary>
public sealed class ReviewProgressReader(IReviewStore cache, IReviewMarkStore marks)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Key(ReviewTarget target) => $"summary:{target.Key}";

    public Task SaveAsync(ReviewTarget target, AnalysisSummary summary, CancellationToken ct) =>
        cache.SetCacheAsync(Key(target), JsonSerializer.Serialize(summary, Json), ct);

    /// <param name="currentHead">The head GitHub reports now (null when unknown, e.g. a local working tree).</param>
    /// <param name="changedLines">Added plus removed lines GitHub reports now.</param>
    public async Task<ReviewProgress> ReadAsync(ReviewTarget target, string? currentHead, int changedLines, CancellationToken ct)
    {
        var progress = new ReviewProgress { ChangedLines = changedLines };
        if (await cache.GetCacheAsync(Key(target), ct).ConfigureAwait(false) is not { } json
            || JsonSerializer.Deserialize<AnalysisSummary>(json, Json) is not { } summary)
        {
            return progress;
        }

        progress.Analysed = true;
        progress.AnalysedHead = summary.Head;
        progress.ToReadLines = summary.OpenLines;
        progress.ChangedLines = changedLines > 0 ? changedLines : summary.ChangedLines;
        progress.OutOfDate = currentHead is { Length: > 0 } head && !string.Equals(head, summary.Head, StringComparison.OrdinalIgnoreCase);
        var marked = (await marks.GetMarksAsync(target, ct).ConfigureAwait(false)).ToDictionary(m => m.Unit, m => m.Fingerprint, StringComparer.Ordinal);
        progress.SlicesTotal = summary.SliceUnits.Count;
        progress.SlicesReviewed = summary.SliceUnits.Count(units => units.Count > 0
            && units.All(u => marked.TryGetValue(u, out var fp) && summary.Fingerprints.TryGetValue(u, out var now) && fp == now));
        return progress;
    }
}
