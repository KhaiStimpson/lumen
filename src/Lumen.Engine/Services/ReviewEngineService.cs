using System.Reflection;
using Grpc.Core;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Engine.Mapping;
using Lumen.Engine.Sessions;
using Lumen.GitHub;

namespace Lumen.Engine.Services;

public sealed class ReviewEngineService(
    PullRequestSessionManager sessions,
    IGitHubClient gitHub,
    IReviewStore store,
    ConnectionsProbe connections,
    ReviewSettingsStore reviewSettings,
    TimeProvider time) : ReviewEngine.ReviewEngineBase
{
    private static readonly string Version =
        typeof(ReviewEngineService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    public override Task<PingReply> Ping(PingRequest request, ServerCallContext context) =>
        Task.FromResult(new PingReply { Version = Version, ProcessId = Environment.ProcessId });

    public override async Task WatchPullRequest(
        WatchPullRequestRequest request,
        IServerStreamWriter<PullRequestEvent> responseStream,
        ServerCallContext context)
    {
        var session = sessions.GetOrStart(ProtoMapper.ToKey(request.PullRequest), request.Refresh);
        await foreach (var evt in session.SubscribeAsync(context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(evt, context.CancellationToken).ConfigureAwait(false);
        }
    }

    public override Task<FileDiff> GetFileDiff(GetFileDiffRequest request, ServerCallContext context)
    {
        var snapshot = RequireSession(request.PullRequest).Snapshot
            ?? throw new RpcException(new Status(StatusCode.FailedPrecondition, "Pull request is still loading."));
        var file = snapshot.FindFile(request.Path)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"'{request.Path}' is not part of this pull request."));
        return Task.FromResult(ProtoMapper.ToProto(file));
    }

    public override async Task<SourceFile> GetSourceFile(GetSourceFileRequest request, ServerCallContext context)
    {
        var checkout = RequireSession(request.PullRequest).Checkout
            ?? throw new RpcException(new Status(StatusCode.FailedPrecondition, "Pull request is still loading."));
        try
        {
            var text = await checkout.ReadHeadFileAsync(request.Path, context.CancellationToken).ConfigureAwait(false);
            return new SourceFile { Path = request.Path, Text = text ?? "", Exists = text is not null };
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<SetReviewPointStateReply> SetReviewPointState(SetReviewPointStateRequest request, ServerCallContext context)
    {
        var session = RequireSession(request.PullRequest);
        await RecordAsync(session, request.ReviewPointId, request.State, null, context.CancellationToken).ConfigureAwait(false);
        return new SetReviewPointStateReply();
    }

    public override async Task<PostReviewCommentReply> PostReviewComment(PostReviewCommentRequest request, ServerCallContext context)
    {
        var session = RequireSession(request.PullRequest);
        var snapshot = session.Snapshot
            ?? throw new RpcException(new Status(StatusCode.FailedPrecondition, "Pull request is still loading."));

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Comment body is empty."));
        }

        if (snapshot.FindFile(request.Path) is not { } file || !file.ContainsNewLine(request.Line))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"{request.Path}:{request.Line} is not part of the diff, so GitHub can't attach a comment there."));
        }

        var posted = await gitHub.PostReviewCommentAsync(
            new NewReviewComment(session.Key, snapshot.HeadSha, request.Path, request.Line, request.Body),
            context.CancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(request.ReviewPointId))
        {
            await RecordAsync(session, request.ReviewPointId, Contracts.ReviewPointState.Commented, posted.Url, context.CancellationToken).ConfigureAwait(false);
        }

        return new PostReviewCommentReply { CommentId = posted.Id, Url = posted.Url };
    }

    public override async Task<SetFileViewedReply> SetFileViewed(SetFileViewedRequest request, ServerCallContext context)
    {
        var session = RequireSession(request.PullRequest);
        var snapshot = session.Snapshot
            ?? throw new RpcException(new Status(StatusCode.FailedPrecondition, "Pull request is still loading."));
        if (snapshot.FindFile(request.Path) is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"'{request.Path}' is not part of this pull request."));
        }

        try
        {
            await gitHub.SetFileViewedAsync(session.Key, request.Path, request.Viewed, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GitHubApiException or GitHubAuthenticationException or HttpRequestException)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, $"GitHub didn't save it: {ex.Message}"));
        }

        // Published so a client that re-attaches replays the tick rather than the snapshot's original state.
        session.Publish(new PullRequestEvent { FileViewed = new FileViewed { Path = request.Path, Viewed = request.Viewed } });
        return new SetFileViewedReply();
    }

    public override Task<Connections> GetConnections(GetConnectionsRequest request, ServerCallContext context) =>
        connections.GetAsync(context.CancellationToken);

    public override async Task<Connections> SetOpenRouterKey(SetOpenRouterKeyRequest request, ServerCallContext context)
    {
        try
        {
            connections.SetOpenRouterKey(request.Key);
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (PlatformNotSupportedException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }

        return await connections.GetAsync(context.CancellationToken).ConfigureAwait(false);
    }

    public override async Task<Connections> RemoveOpenRouterKey(RemoveOpenRouterKeyRequest request, ServerCallContext context)
    {
        connections.RemoveOpenRouterKey();
        return await connections.GetAsync(context.CancellationToken).ConfigureAwait(false);
    }

    public override async Task<Connections> UpdateConnectionSettings(ConnectionSettings request, ServerCallContext context)
    {
        try
        {
            connections.UpdateSettings(request);
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, $"Could not save settings: {ex.Message}"));
        }

        return await connections.GetAsync(context.CancellationToken).ConfigureAwait(false);
    }

    public override Task<ReviewSettingsReply> GetReviewSettings(ReviewSettingsRequest request, ServerCallContext context)
    {
        try
        {
            return Task.FromResult(ReviewSettingsReply(RepositoryOf(request.Owner, request.Name)));
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override Task<ReviewSettingsReply> UpdateReviewSettings(UpdateReviewSettingsRequest request, ServerCallContext context)
    {
        var repository = RepositoryOf(request.Owner, request.Name);
        var rules = request.Rules ?? new ReviewRules();
        try
        {
            if (repository is null)
            {
                reviewSettings.UpdateGlobal(new Analysis.ReviewSettings
                {
                    Sensitivity = FromProto(rules.Sensitivity) ?? Analysis.ReviewSensitivity.Balanced,
                    IgnoredNames = [.. rules.IgnoredNames],
                    MechanicalPaths = [.. rules.MechanicalPaths],
                    SkippedPaths = [.. rules.SkippedPaths],
                });
            }
            else
            {
                reviewSettings.Update(repository, new RepositoryReviewSettings
                {
                    Sensitivity = FromProto(rules.Sensitivity),
                    IgnoredNames = [.. rules.IgnoredNames],
                    MechanicalPaths = [.. rules.MechanicalPaths],
                    SkippedPaths = [.. rules.SkippedPaths],
                });
            }
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, $"Could not save settings: {ex.Message}"));
        }

        return Task.FromResult(ReviewSettingsReply(repository));
    }

    private ReviewSettingsReply ReviewSettingsReply(RepositoryRef? repository)
    {
        var global = reviewSettings.Global;
        var reply = new ReviewSettingsReply
        {
            Global = ToProto(global.Sensitivity, global.IgnoredNames, global.MechanicalPaths, global.SkippedPaths),
            SettingsPath = reviewSettings.SettingsPath,
            Quiet = ToProto(Analysis.ReviewSensitivity.Quiet),
            Balanced = ToProto(Analysis.ReviewSensitivity.Balanced),
            Thorough = ToProto(Analysis.ReviewSensitivity.Thorough),
        };
        reply.BuiltInIgnoredNames.AddRange(Roslyn.PeerPatternDetector.BuiltInIgnoredNames.Order(StringComparer.OrdinalIgnoreCase));
        reply.BuiltInMechanicalPaths.AddRange(Analysis.MechanicalClassifier.BuiltInDescriptions);

        if (repository is not null)
        {
            var own = reviewSettings.Get(repository);
            reply.Repository = ToProto(own.Sensitivity, own.IgnoredNames, own.MechanicalPaths, own.SkippedPaths);
            reply.RepositorySettingsPath = reviewSettings.PathFor(repository);
        }

        return reply;
    }

    private static RepositoryRef? RepositoryOf(string owner, string name)
    {
        if (owner.Length == 0 && name.Length == 0)
        {
            return null;
        }

        if (owner.Length == 0 || name.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Give both the repository owner and name, or neither."));
        }

        return new RepositoryRef(owner, name);
    }

    private static ReviewRules ToProto(
        Analysis.ReviewSensitivity? sensitivity,
        IEnumerable<string> ignoredNames,
        IEnumerable<string> mechanicalPaths,
        IEnumerable<string> skippedPaths)
    {
        var rules = new ReviewRules { Sensitivity = sensitivity is null ? null : ToProto(sensitivity) };
        rules.IgnoredNames.AddRange(ignoredNames);
        rules.MechanicalPaths.AddRange(mechanicalPaths);
        rules.SkippedPaths.AddRange(skippedPaths);
        return rules;
    }

    private static Contracts.ReviewSensitivity ToProto(Analysis.ReviewSensitivity sensitivity) => new()
    {
        MinimumPeers = sensitivity.MinimumPeers,
        MinimumSupport = sensitivity.MinimumSupport,
        MinimumLift = sensitivity.MinimumLift,
        MaxPointsPerType = sensitivity.MaxPointsPerType,
        MaxExamples = sensitivity.MaxExamples,
    };

    private static Analysis.ReviewSensitivity? FromProto(Contracts.ReviewSensitivity? sensitivity) => sensitivity is null ? null : new()
    {
        MinimumPeers = sensitivity.MinimumPeers,
        MinimumSupport = sensitivity.MinimumSupport,
        MinimumLift = sensitivity.MinimumLift,
        MaxPointsPerType = sensitivity.MaxPointsPerType,
        MaxExamples = sensitivity.MaxExamples,
    };

    private async Task RecordAsync(
        PullRequestSession session,
        string reviewPointId,
        Contracts.ReviewPointState state,
        string? detail,
        CancellationToken cancellationToken)
    {
        if (session.FindReviewPoint(reviewPointId) is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Unknown review point '{reviewPointId}'."));
        }

        await store.RecordInteractionAsync(
            new ReviewInteraction(session.Key, session.Snapshot?.HeadSha ?? "", reviewPointId, ProtoMapper.ToAction(state), time.GetUtcNow(), detail),
            cancellationToken).ConfigureAwait(false);

        session.Publish(new PullRequestEvent
        {
            ReviewPointStateChanged = new ReviewPointStateChanged { ReviewPointId = reviewPointId, State = state },
        });
    }

    private PullRequestSession RequireSession(PullRequestRef pullRequest) =>
        sessions.Find(ProtoMapper.ToKey(pullRequest))
        ?? throw new RpcException(new Status(StatusCode.NotFound, "Pull request is not open. Call WatchPullRequest first."));
}
