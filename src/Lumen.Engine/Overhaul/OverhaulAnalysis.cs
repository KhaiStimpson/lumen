using Lumen.Analysis;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Engine.Sessions;

namespace Lumen.Engine.Overhaul;

/// <summary>
/// The overhaul's analysis after triage (docs/overhaul.md): feature slices, red flags and reviewed marks. Every part
/// is best effort: a failure is logged and the review goes on as today's file list with the mechanical folds.
/// </summary>
public sealed partial class OverhaulAnalysis(IReviewMarkStore marks, ILogger<OverhaulAnalysis> logger)
{
    public async Task RunAsync(
        PullRequestSession session,
        Domain.PullRequestSnapshot snapshot,
        IPullRequestCheckout checkout,
        ReviewSettings settings,
        TriageResult? triage,
        CancellationToken cancellationToken)
    {
        await PublishMarksAsync(session, cancellationToken).ConfigureAwait(false);
    }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "{PullRequest}: couldn't read reviewed marks")]
    private static partial void LogMarksUnavailable(ILogger logger, Exception exception, PullRequestKey pullRequest);
}
