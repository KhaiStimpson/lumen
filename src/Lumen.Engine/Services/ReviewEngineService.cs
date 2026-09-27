using System.Reflection;
using Grpc.Core;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Engine.Mapping;
using Lumen.Engine.Sessions;

namespace Lumen.Engine.Services;

public sealed class ReviewEngineService(
    PullRequestSessionManager sessions,
    IGitHubClient gitHub,
    IReviewStore store,
    ConnectionsProbe connections,
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
