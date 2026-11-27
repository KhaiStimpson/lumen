using System.Runtime.CompilerServices;
using Google.Protobuf;
using Lumen.Contracts;

namespace Lumen.App.Services;

/// <summary>
/// Replays a recorded engine session (events.jsonl + diffs.jsonl). Used by UI tests and by <c>--fixture</c>
/// for design work without GitHub. Writes are accepted and echoed back as events; nothing leaves the machine.
/// </summary>
public sealed partial class FixtureReviewSource : IReviewSource
{
    private readonly List<PullRequestEvent> _events;
    private readonly Dictionary<string, FileDiff> _diffs;
    private readonly List<System.Threading.Channels.Channel<PullRequestEvent>> _live = [];

    public FixtureReviewSource(string directory, TimeSpan? eventDelay = null, string? viewerLogin = null)
    {
        var parser = new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
        _events = [.. File.ReadAllLines(Path.Combine(directory, "events.jsonl")).Where(l => l.Length > 0).Select(parser.Parse<PullRequestEvent>)];
        _diffs = File.ReadAllLines(Path.Combine(directory, "diffs.jsonl"))
            .Where(l => l.Length > 0)
            .Select(parser.Parse<FileDiff>)
            .ToDictionary(d => d.Path, StringComparer.Ordinal);
        EventDelay = eventDelay ?? TimeSpan.Zero;

        if (viewerLogin is not null)
        {
            foreach (var evt in _events.Where(e => e.Snapshot is not null))
            {
                evt.Snapshot.ViewerLogin = viewerLogin;
            }
        }

        LoadV2(directory, parser, viewerLogin);
    }

    public TimeSpan EventDelay { get; }

    public List<PostReviewCommentRequest> PostedComments { get; } = [];

    public string Description => "Recorded fixture";

    public async IAsyncEnumerable<PullRequestEvent> WatchAsync(
        PullRequestRef pullRequest,
        bool refresh,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var live = System.Threading.Channels.Channel.CreateUnbounded<PullRequestEvent>();
        lock (_live)
        {
            _live.Add(live);
        }

        foreach (var evt in _events.Concat(CurrentState()))
        {
            if (EventDelay > TimeSpan.Zero)
            {
                await Task.Delay(EventDelay, cancellationToken).ConfigureAwait(false);
            }

            yield return evt;
        }

        await foreach (var evt in live.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <summary>Every file diff request, so tests can see ranges and whitespace flags.</summary>
    public List<GetFileDiffRequest> DiffRequests { get; } = [];

    public Task<FileDiff> GetFileDiffAsync(GetFileDiffRequest request, CancellationToken cancellationToken)
    {
        lock (DiffRequests)
        {
            DiffRequests.Add(request.Clone());
        }

        // The recording has whole-PR diffs only; a range or whitespace-insensitive request gets the same hunks.
        return Task.FromResult(_diffs.TryGetValue(request.Path, out var diff) ? diff : new FileDiff { Path = request.Path });
    }

    public Task<SourceFile> GetSourceFileAsync(PullRequestRef pullRequest, string path, CancellationToken cancellationToken, bool fromBase = false)
    {
        if (fromBase)
        {
            // The recording has no whole base files. Keep the recorded precedent snippet as the proof.
            return Task.FromResult(new SourceFile { Path = path, Exists = false });
        }

        // Reconstruct head-side text from the diff when available; precedent files usually aren't in the diff.
        if (_diffs.TryGetValue(path, out var diff))
        {
            // Lines outside the hunks weren't recorded: leave them blank so line numbers still match the head.
            var known = diff.Hunks.SelectMany(h => h.Lines).Where(l => l.NewNumber > 0).DistinctBy(l => l.NewNumber).ToDictionary(l => l.NewNumber, l => l.Text);
            var lines = Enumerable.Range(1, known.Keys.DefaultIfEmpty(0).Max()).Select(n => known.GetValueOrDefault(n, ""));
            return Task.FromResult(new SourceFile { Path = path, Text = string.Join('\n', lines), Exists = true });
        }

        // Precedent files aren't in the recording; the UI falls back to the recorded snippet.
        return Task.FromResult(new SourceFile { Path = path, Exists = false });
    }

    public Task<PostReviewCommentReply> PostCommentAsync(PostReviewCommentRequest request, CancellationToken cancellationToken)
    {
        // Like GitHub: no single comments while a pending review exists, so it joins the review instead.
        if (_pending.Id.Length > 0)
        {
            var pending = AddPending(request.Path, request.Side, request.Line, request.StartLine, request.Body, null);
            return Task.FromResult(new PostReviewCommentReply { AddedToPendingReview = true, PendingReview = pending });
        }

        PostedComments.Add(request);
        AddThread(request.Path, request.Side, request.Line, request.StartLine, request.Body, pending: false);
        return Task.FromResult(new PostReviewCommentReply { CommentId = PostedComments.Count, Url = "https://example.invalid/fixture-comment" });
    }

    public Task SetFileViewedAsync(PullRequestRef pullRequest, string path, bool viewed, CancellationToken cancellationToken)
    {
        Broadcast(new PullRequestEvent { FileViewed = new FileViewed { Path = path, Viewed = viewed } });
        return Task.CompletedTask;
    }

    /// <summary>The global review layer; starts with nothing added.</summary>
    public ReviewRules GlobalReviewRules { get; private set; } = new();

    /// <summary>Repository layers by "owner/name". Missing means the repository inherits everything.</summary>
    public Dictionary<string, ReviewRules> RepositoryReviewRules { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<ReviewSettingsReply> GetReviewSettingsAsync(ReviewSettingsRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(FixtureReviewSettings(request.Owner, request.Name));

    public Task<ReviewSettingsReply> UpdateReviewSettingsAsync(UpdateReviewSettingsRequest request, CancellationToken cancellationToken)
    {
        var rules = request.Rules?.Clone() ?? new ReviewRules();
        if (request.Owner.Length == 0)
        {
            GlobalReviewRules = rules;
        }
        else
        {
            RepositoryReviewRules[$"{request.Owner}/{request.Name}"] = rules;
        }

        return Task.FromResult(FixtureReviewSettings(request.Owner, request.Name));
    }

    private ReviewSettingsReply FixtureReviewSettings(string owner, string name)
    {
        var reply = new ReviewSettingsReply
        {
            Global = GlobalReviewRules.Clone(),
            SettingsPath = @"%LOCALAPPDATA%\Lumen\settings.json",
        };
        reply.BuiltInMechanicalPaths.AddRange(["*.Designer.cs", "*.g.cs", "*.generated.cs", "*ModelSnapshot.cs", "Migrations/*.cs", "lock files", "<auto-generated> headers"]);
        if (owner.Length > 0)
        {
            reply.Repository = RepositoryReviewRules.TryGetValue($"{owner}/{name}", out var own) ? own.Clone() : new ReviewRules();
            reply.RepositorySettingsPath = $@"%LOCALAPPDATA%\Lumen\review\{owner}\{name}.json";
        }

        return reply;
    }

    private void Broadcast(PullRequestEvent evt)
    {
        lock (_live)
        {
            foreach (var channel in _live)
            {
                channel.Writer.TryWrite(evt);
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
