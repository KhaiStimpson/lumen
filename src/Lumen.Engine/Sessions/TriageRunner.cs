using System.Security.Cryptography;
using System.Text;
using Lumen.Analysis;
using Lumen.Domain;
using Lumen.Roslyn.Triage;

namespace Lumen.Engine.Sessions;

/// <summary>
/// Computes a pull request's triage once per head commit and serves it from the store afterwards, so reopening a
/// PR is free. Never breaks the review: any failure is logged and the review goes on without triage.
/// </summary>
public sealed partial class TriageRunner(ITriageStore store, ILogger<TriageRunner> logger)
{
    /// <summary>Bump when a classifier changes what it proves, so cached triage from older rules is not reused.</summary>
    public const string ClassifierVersion = "triage/2";

    /// <summary>The cache version: the classifier version plus the repository's own mechanical path rules.</summary>
    public static string VersionFor(ReviewSettings settings)
    {
        var rules = string.Join("\n", settings.MechanicalPaths);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rules)))[..12];
        return $"{ClassifierVersion}:{hash}";
    }

    public async Task<TriageResult?> RunAsync(
        PullRequestSnapshot snapshot,
        IPullRequestCheckout checkout,
        ReviewSettings settings,
        CancellationToken cancellationToken)
    {
        var version = VersionFor(settings);
        try
        {
            if (await store.FindTriageAsync(snapshot.Key, snapshot.HeadSha, version, cancellationToken).ConfigureAwait(false) is { } cached)
            {
                return cached;
            }

            var sources = await TriageSources.LoadAsync(snapshot, checkout, cancellationToken).ConfigureAwait(false);
            var result = await Task.Run(() => RoslynTriage.CreatePipeline().Run(snapshot, sources), cancellationToken).ConfigureAwait(false);
            await store.SaveTriageAsync(snapshot.Key, snapshot.HeadSha, version, result, cancellationToken).ConfigureAwait(false);
            LogTriaged(logger, snapshot.Key, result.Hunks.Count, result.Groups.Count);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogTriageFailed(logger, ex, snapshot.Key);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{PullRequest}: triaged {Hunks} hunks into {Groups} groups")]
    private static partial void LogTriaged(ILogger logger, PullRequestKey pullRequest, int hunks, int groups);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{PullRequest}: triage failed; the review continues without it")]
    private static partial void LogTriageFailed(ILogger logger, Exception exception, PullRequestKey pullRequest);
}
