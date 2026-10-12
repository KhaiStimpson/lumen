using System.Collections.Concurrent;
using System.Text.Json;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Engine.Mapping;
using Lumen.Engine.Sessions;
using Lumen.Models;
using Lumen.Models.Walkthrough;
using Walkthrough = Lumen.Domain.Walkthrough;
using WalkthroughStatus = Lumen.Domain.WalkthroughStatus;

namespace Lumen.Engine.Walkthroughs;

/// <summary>
/// Runs the PR walkthrough (plan v2 M8) for open pull requests: automatically once the diff is shown, and on request
/// for "since last review". Results stream to the pull request's subscribers as <c>WalkthroughUpdated</c> and are
/// cached per head commit and settings, so reopening costs nothing. A review never waits on it: if the fast model is
/// off, missing or failing, the walkthrough says so in one line and everything else works the same.
/// </summary>
public sealed partial class WalkthroughCoordinator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IModelProvider _model;
    private readonly EngineSettingsStore _settings;
    private readonly IWalkthroughStore _cache;
    private readonly ReviewActions _actions;
    private readonly ILogger<WalkthroughCoordinator> _logger;
    private readonly ConcurrentDictionary<(PullRequestSession Session, string Scope), Task> _running = new();

    public WalkthroughCoordinator(
        IModelProvider model,
        EngineSettingsStore settings,
        IWalkthroughStore cache,
        ReviewActions actions,
        PullRequestSessionManager sessions,
        ILogger<WalkthroughCoordinator> logger)
    {
        _model = model;
        _settings = settings;
        _cache = cache;
        _actions = actions;
        _logger = logger;
        sessions.SessionReady += session => Start(session, null, refresh: false);
    }

    /// <summary>Why the walkthrough isn't offered, or null when it is.</summary>
    public string? Unavailable()
    {
        var privacy = _settings.Current.Privacy;
        if (!privacy.AllowCloudReasoning || privacy.LocalModelsOnly)
        {
            return "Cloud AI is turned off.";
        }

        if (!privacy.AllowCodeToFastModel)
        {
            return "Turn on the walkthrough in Connections to send the diff to the fast model.";
        }

        return _model.HasCredentials ? null : "Connect Cloudflare Workers AI in Connections for a walkthrough.";
    }

    public WalkthroughGenerator Generator()
    {
        var fast = _settings.Current.FastModel;
        return new WalkthroughGenerator(_model, new WalkthroughOptions
        {
            MaxDiffBytes = Math.Max(16, fast.MaxDiffKb) * 1024,
            MaxConcurrent = Math.Clamp(fast.MaxConcurrent, 1, 16),
        });
    }

    /// <summary>Starts (or joins) a walkthrough; results arrive on the session's stream.</summary>
    public void Start(PullRequestSession session, string? sinceSha, bool refresh)
    {
        ArgumentNullException.ThrowIfNull(session);
        var scope = sinceSha is null ? "full" : $"since:{sinceSha}";
        if (Unavailable() is { } reason)
        {
            Publish(session, new Walkthrough { Status = WalkthroughStatus.Unavailable, Detail = reason, HeadSha = session.Snapshot?.HeadSha ?? "", SinceSha = sinceSha });
            return;
        }

        var key = (session, scope);
        if (!refresh && _running.TryGetValue(key, out var running) && !running.IsCompleted)
        {
            return;
        }

        _running[key] = Task.Run(() => RunAsync(session, sinceSha, scope, refresh), CancellationToken.None);
    }

    private async Task RunAsync(PullRequestSession session, string? sinceSha, string scope, bool refresh)
    {
        var ct = session.Cancellation;
        var snapshot = session.Snapshot;
        if (snapshot is null)
        {
            return;
        }

        var generator = Generator();
        try
        {
            if (!refresh && await _cache.FindWalkthroughAsync(session.Key, snapshot.HeadSha, scope, generator.SettingsHash, ct).ConfigureAwait(false) is { } json
                && JsonSerializer.Deserialize<Walkthrough>(json, Json) is { Status: WalkthroughStatus.Complete } cached)
            {
                Publish(session, cached with { FromCache = true });
                return;
            }

            IReadOnlyList<ChangedFile> files = snapshot.Files;
            if (sinceSha is not null)
            {
                files = (await _actions.GetRangeAsync(session, sinceSha, snapshot.HeadSha, false, ct).ConfigureAwait(false)).Files;
            }

            var details = session.Details;
            var input = new WalkthroughInput
            {
                Title = snapshot.Metadata.Title,
                Description = snapshot.Metadata.Body ?? "",
                CommitHeadlines = details?.Commits.Select(c => c.Headline).ToList() ?? [],
                Files = files,
                HeadSha = snapshot.HeadSha,
                SinceSha = sinceSha,
            };

            // The finished walkthrough is cached before it is announced, so a reopen right after never misses it.
            var result = await generator.RunAsync(
                input,
                w =>
                {
                    if (w.Status != WalkthroughStatus.Complete)
                    {
                        Publish(session, w);
                    }
                },
                ct).ConfigureAwait(false);
            if (result.Status == WalkthroughStatus.Complete)
            {
                await _cache.SaveWalkthroughAsync(session.Key, snapshot.HeadSha, scope, generator.SettingsHash, JsonSerializer.Serialize(result, Json), ct).ConfigureAwait(false);
                Publish(session, result);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogWalkthroughFailed(_logger, ex, session.Key);
            var reason = ex is ModelException model ? model.Message : ex is ReviewActionException action ? action.Message : "The walkthrough stopped.";
            Publish(session, new Walkthrough
            {
                Status = WalkthroughStatus.Failed,
                Detail = $"{reason} The review works as usual.",
                HeadSha = snapshot.HeadSha,
                SinceSha = sinceSha,
                Producer = generator.Producer,
            });
        }
    }

    private static void Publish(PullRequestSession session, Walkthrough walkthrough) =>
        session.Publish(new PullRequestEvent { WalkthroughUpdated = new WalkthroughUpdated { Walkthrough = ProtoMapper.ToProto(walkthrough) } });

    [LoggerMessage(Level = LogLevel.Warning, Message = "{PullRequest}: walkthrough failed; the review is unaffected")]
    private static partial void LogWalkthroughFailed(ILogger logger, Exception exception, PullRequestKey pullRequest);
}
