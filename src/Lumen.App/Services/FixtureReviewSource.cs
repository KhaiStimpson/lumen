using System.Runtime.CompilerServices;
using Google.Protobuf;
using Lumen.Contracts;

namespace Lumen.App.Services;

/// <summary>
/// Replays a recorded engine session (events.jsonl + diffs.jsonl). Used by UI tests and by <c>--fixture</c>
/// for design work without GitHub. Writes are accepted and echoed back as events; nothing leaves the machine.
/// </summary>
public sealed class FixtureReviewSource : IReviewSource
{
    private readonly List<PullRequestEvent> _events;
    private readonly Dictionary<string, FileDiff> _diffs;
    private readonly List<System.Threading.Channels.Channel<PullRequestEvent>> _live = [];

    public FixtureReviewSource(string directory, TimeSpan? eventDelay = null)
    {
        var parser = new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
        _events = [.. File.ReadAllLines(Path.Combine(directory, "events.jsonl")).Where(l => l.Length > 0).Select(parser.Parse<PullRequestEvent>)];
        _diffs = File.ReadAllLines(Path.Combine(directory, "diffs.jsonl"))
            .Where(l => l.Length > 0)
            .Select(parser.Parse<FileDiff>)
            .ToDictionary(d => d.Path, StringComparer.Ordinal);
        EventDelay = eventDelay ?? TimeSpan.Zero;
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

        foreach (var evt in _events)
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

    public Task<FileDiff> GetFileDiffAsync(PullRequestRef pullRequest, string path, CancellationToken cancellationToken) =>
        Task.FromResult(_diffs.TryGetValue(path, out var diff) ? diff : new FileDiff { Path = path });

    public Task<SourceFile> GetSourceFileAsync(PullRequestRef pullRequest, string path, CancellationToken cancellationToken)
    {
        // Reconstruct head-side text from the diff when available; precedent files usually aren't in the diff.
        if (_diffs.TryGetValue(path, out var diff))
        {
            var lines = diff.Hunks.SelectMany(h => h.Lines).Where(l => l.NewNumber > 0).Select(l => l.Text);
            return Task.FromResult(new SourceFile { Path = path, Text = string.Join('\n', lines), Exists = true });
        }

        // Precedent files aren't in the recording; the UI falls back to the recorded snippet.
        return Task.FromResult(new SourceFile { Path = path, Exists = false });
    }

    public Task SetReviewPointStateAsync(PullRequestRef pullRequest, string reviewPointId, ReviewPointState state, CancellationToken cancellationToken)
    {
        Broadcast(new PullRequestEvent
        {
            ReviewPointStateChanged = new ReviewPointStateChanged { ReviewPointId = reviewPointId, State = state },
        });
        return Task.CompletedTask;
    }

    public Task<PostReviewCommentReply> PostCommentAsync(PostReviewCommentRequest request, CancellationToken cancellationToken)
    {
        PostedComments.Add(request);
        if (!string.IsNullOrEmpty(request.ReviewPointId))
        {
            Broadcast(new PullRequestEvent
            {
                ReviewPointStateChanged = new ReviewPointStateChanged { ReviewPointId = request.ReviewPointId, State = ReviewPointState.Commented },
            });
        }

        return Task.FromResult(new PostReviewCommentReply { CommentId = PostedComments.Count, Url = "https://example.invalid/fixture-comment" });
    }

    /// <summary>Whether a key has been "stored"; the fixture keeps only this flag, never the key.</summary>
    public bool OpenRouterKeyStored { get; set; }

    public ConnectionSettings ConnectionSettings { get; private set; } = new()
    {
        AllowCloudReasoning = true,
        JevEnabled = true,
        JevModel = "typesafe/jev-1.13",
        JevTimeoutSeconds = 12,
        JevRequireZeroDataRetention = true,
        MaxInvestigationsPerPullRequest = 3,
        MaxConcurrentInvestigations = 1,
        AllowMeteredUsage = false,
    };

    public Task<Connections> GetConnectionsAsync(CancellationToken cancellationToken) => Task.FromResult(FixtureConnections());

    /// <summary>Like the engine, a limit left unset keeps its current value.</summary>
    public Task<Connections> UpdateConnectionSettingsAsync(ConnectionSettings settings, CancellationToken cancellationToken)
    {
        var next = ConnectionSettings.Clone();
        next.AllowCloudReasoning = settings.AllowCloudReasoning;
        next.JevEnabled = settings.JevEnabled;
        next.AllowCodeSnippetsToJev = settings.AllowCodeSnippetsToJev;
        next.InvestigationsEnabled = settings.InvestigationsEnabled;
        if (settings.HasJevModel)
        {
            next.JevModel = settings.JevModel;
        }

        if (settings.HasJevTimeoutSeconds)
        {
            next.JevTimeoutSeconds = settings.JevTimeoutSeconds;
        }

        if (settings.HasJevRequireZeroDataRetention)
        {
            next.JevRequireZeroDataRetention = settings.JevRequireZeroDataRetention;
        }

        if (settings.HasMaxInvestigationsPerPullRequest)
        {
            next.MaxInvestigationsPerPullRequest = settings.MaxInvestigationsPerPullRequest;
        }

        if (settings.HasMaxConcurrentInvestigations)
        {
            next.MaxConcurrentInvestigations = settings.MaxConcurrentInvestigations;
        }

        if (settings.HasAllowMeteredUsage)
        {
            next.AllowMeteredUsage = settings.AllowMeteredUsage;
        }

        ConnectionSettings = next;
        return Task.FromResult(FixtureConnections());
    }

    /// <summary>The global review layer; starts at Balanced with nothing added.</summary>
    public ReviewRules GlobalReviewRules { get; private set; } = new()
    {
        Sensitivity = new ReviewSensitivity { MinimumPeers = 3, MinimumSupport = 0.75, MinimumLift = 1.5, MaxPointsPerType = 2, MaxExamples = 4 },
    };

    /// <summary>Repository layers by "owner/name". Missing means the repository inherits everything.</summary>
    public Dictionary<string, ReviewRules> RepositoryReviewRules { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<ReviewSettingsReply> GetReviewSettingsAsync(ReviewSettingsRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(FixtureReviewSettings(request.Owner, request.Name));

    public Task<ReviewSettingsReply> UpdateReviewSettingsAsync(UpdateReviewSettingsRequest request, CancellationToken cancellationToken)
    {
        var rules = request.Rules?.Clone() ?? new ReviewRules();
        if (request.Owner.Length == 0)
        {
            rules.Sensitivity ??= GlobalReviewRules.Sensitivity;
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
            Quiet = new ReviewSensitivity { MinimumPeers = 5, MinimumSupport = 0.85, MinimumLift = 2.0, MaxPointsPerType = 1, MaxExamples = 3 },
            Balanced = new ReviewSensitivity { MinimumPeers = 3, MinimumSupport = 0.75, MinimumLift = 1.5, MaxPointsPerType = 2, MaxExamples = 4 },
            Thorough = new ReviewSensitivity { MinimumPeers = 3, MinimumSupport = 0.65, MinimumLift = 1.2, MaxPointsPerType = 3, MaxExamples = 6 },
        };
        reply.BuiltInIgnoredNames.AddRange(["CancellationToken", "Guid", "ILogger<T>", "string.IsNullOrEmpty", "Task.WhenAll", "TimeProvider"]);
        reply.BuiltInMechanicalPaths.AddRange(["*.Designer.cs", "*.g.cs", "*.generated.cs", "*ModelSnapshot.cs", "Migrations/*.cs", "lock files", "<auto-generated> headers"]);
        if (owner.Length > 0)
        {
            reply.Repository = RepositoryReviewRules.TryGetValue($"{owner}/{name}", out var own) ? own.Clone() : new ReviewRules();
            reply.RepositorySettingsPath = $@"%LOCALAPPDATA%\Lumen\review\{owner}\{name}.json";
        }

        return reply;
    }

    public Task<Connections> SetOpenRouterKeyAsync(string key, CancellationToken cancellationToken)
    {
        OpenRouterKeyStored = !string.IsNullOrWhiteSpace(key);
        return Task.FromResult(FixtureConnections());
    }

    public Task<Connections> RemoveOpenRouterKeyAsync(CancellationToken cancellationToken)
    {
        OpenRouterKeyStored = false;
        return Task.FromResult(FixtureConnections());
    }

    private Connections FixtureConnections() => new()
    {
        Settings = ConnectionSettings.Clone(),
        Jev = new JevConnection
        {
            State = !ConnectionSettings.AllowCloudReasoning || !ConnectionSettings.JevEnabled ? ConnectionState.Off
                : OpenRouterKeyStored ? ConnectionState.Connected : ConnectionState.NotConnected,
            Detail = !ConnectionSettings.AllowCloudReasoning ? "Off: cloud AI is turned off"
                : !ConnectionSettings.JevEnabled ? "Turned off"
                : OpenRouterKeyStored ? "Key valid · 4.82 credit remaining" : "No OpenRouter key",
            Model = "typesafe/jev-1.13",
            KeyStored = OpenRouterKeyStored,
            CanStoreKey = true,
            Sends = ConnectionSettings.AllowCodeSnippetsToJev
                ? "Counts, categories and convention wording"
                : "Counts and categories only — no code, paths or names",
        },
        Claude = new AgentConnection
        {
            State = ConnectionState.Connected,
            Detail = "Claude Pro via Claude Code",
            Version = "2.1.0",
            Billing = ConnectionBilling.Subscription,
            InvestigationsEnabled = ConnectionSettings.AllowCloudReasoning && ConnectionSettings.InvestigationsEnabled,
            InvestigationsDetail = ConnectionSettings.AllowCloudReasoning && ConnectionSettings.InvestigationsEnabled
                ? "On · up to 3 per pull request · subscription only"
                : "Off",
        },
        SettingsPath = @"%LOCALAPPDATA%\Lumen\settings.json",
    };

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
