using System.Collections.Concurrent;
using Lumen.Domain;

namespace Lumen.Engine.Tests;

internal sealed class FakeGitHub : IGitHubClient
{
    public ConcurrentBag<NewReviewComment> Posted { get; } = [];

    public Exception? FailWith { get; set; }

    public Task<string> GetViewerLoginAsync(CancellationToken cancellationToken) => Task.FromResult("reviewer");

    public Task<PullRequestInfo> GetPullRequestAsync(PullRequestKey key, CancellationToken cancellationToken) =>
        FailWith is not null
            ? Task.FromException<PullRequestInfo>(FailWith)
            : Task.FromResult(new PullRequestInfo(
                key,
                new PullRequestMetadata("Add invoice retrying", Author, "open", false, "main", "feature/retry", "https://github.com/acme/billing/pull/42", null, DateTimeOffset.UtcNow),
                WorkerScenario.BaseSha,
                WorkerScenario.HeadSha));

    private readonly Lock _gate = new();
    private int _ids;

    /// <summary>Threads GitHub holds; pending ones belong to <see cref="Pending"/>.</summary>
    public List<ReviewThread> Threads { get; } = [];

    public PendingReview? Pending { get; set; }

    public List<(string ReviewId, ReviewEvent Event, string Body)> Submitted { get; } = [];

    public List<TimelineItem> IssueComments { get; } = [];

    public List<(IReadOnlyList<InboxQuery> Queries, int Call)> InboxSearches { get; } = [];

    public Func<InboxQuery, IReadOnlyList<InboxItem>> InboxResults { get; set; } = _ => [];

    public Func<PullRequestKey, PullRequestDetails>? Details { get; set; }

    public string Author { get; set; } = "sarah-chen";

    public Task<ReviewThreadsResult> GetReviewThreadsAsync(PullRequestKey key, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(new ReviewThreadsResult([.. Threads], Pending));
        }
    }

    public Task<PostedComment> PostReviewCommentAsync(NewReviewComment comment, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Pending is not null)
            {
                return Task.FromException<PostedComment>(new PendingReviewExistsException());
            }

            Posted.Add(comment);
            Threads.Add(Thread(comment, pending: false, out _));
        }

        return Task.FromResult(new PostedComment(1001, "https://github.com/acme/billing/pull/42#discussion_r1001"));
    }

    private ReviewThread Thread(NewReviewComment comment, bool pending, out string commentId)
    {
        commentId = $"C_{++_ids}";
        return new ReviewThread
        {
            Id = $"T_{_ids}",
            Path = comment.Path,
            Side = comment.Side,
            Line = comment.Line,
            StartLine = comment.StartLine,
            OriginalLine = comment.Line,
            CanResolve = true,
            Comments = [new ThreadComment { Id = commentId, Author = "reviewer", Body = comment.Body, CreatedAt = DateTimeOffset.UtcNow, IsPending = pending, ReviewId = Pending?.Id }],
        };
    }

    public Task<InboxSearchResult> SearchInboxAsync(IReadOnlyList<InboxQuery> queries, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            InboxSearches.Add((queries, InboxSearches.Count + 1));
        }

        return FailWith is not null
            ? Task.FromException<InboxSearchResult>(FailWith)
            : Task.FromResult(new InboxSearchResult("reviewer", queries.ToDictionary(q => q.Id, InboxResults)));
    }

    public Task<PullRequestDetails> GetPullRequestDetailsAsync(PullRequestKey key, CancellationToken cancellationToken) =>
        Task.FromResult(Details?.Invoke(key) ?? new PullRequestDetails
        {
            Key = key,
            Title = "Add invoice retrying",
            Author = Author,
            Body = "Retries failed invoices.",
            HeadSha = WorkerScenario.HeadSha,
            ViewerLogin = "reviewer",
            Timeline = [.. IssueComments],
            Commits = [new CommitInfo(WorkerScenario.HeadSha, "Add retry worker", "Add retry worker", "sarah-chen", DateTimeOffset.UtcNow)],
            Checks = [new CheckRunInfo("build", CheckState.Success, "SUCCESS", null, null, "https://ci", false)],
        });

    public Task<TimelineItem> PostIssueCommentAsync(PullRequestKey key, string body, CancellationToken cancellationToken)
    {
        var item = new TimelineItem { Id = $"IC_{IssueComments.Count + 1}", Kind = TimelineItemKind.Comment, Author = "reviewer", Body = body, CreatedAt = DateTimeOffset.UtcNow };
        IssueComments.Add(item);
        return Task.FromResult(item);
    }

    public Task<ThreadComment> ReplyToThreadAsync(string threadId, string body, string? pendingReviewId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var index = Threads.FindIndex(t => t.Id == threadId);
            var comment = new ThreadComment { Id = $"C_{++_ids}", Author = "reviewer", Body = body, CreatedAt = DateTimeOffset.UtcNow, IsPending = pendingReviewId is not null, ReviewId = pendingReviewId };
            Threads[index] = Threads[index] with { Comments = [.. Threads[index].Comments, comment] };
            if (pendingReviewId is not null && Pending is { } pending)
            {
                Pending = pending with { Comments = [.. pending.Comments, new PendingComment { Id = comment.Id, ThreadId = threadId, Path = Threads[index].Path, Body = body, IsReply = true }] };
            }

            return Task.FromResult(comment);
        }
    }

    public Task SetThreadResolvedAsync(string threadId, bool resolved, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var index = Threads.FindIndex(t => t.Id == threadId);
            Threads[index] = Threads[index] with { IsResolved = resolved };
        }

        return Task.CompletedTask;
    }

    public Task<string> StartPendingReviewAsync(PullRequestKey key, string commitSha, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Pending is not null)
            {
                return Task.FromException<string>(new InvalidOperationException("A second pending review"));
            }

            Pending = new PendingReview { Id = $"PRR_{++_ids}" };
            return Task.FromResult(Pending.Id);
        }
    }

    public Task<string> AddPendingThreadAsync(string reviewId, NewReviewComment comment, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var thread = Thread(comment, pending: true, out var commentId);
            Threads.Add(thread);
            Pending = Pending! with
            {
                Comments = [.. Pending.Comments, new PendingComment { Id = commentId, ThreadId = thread.Id, Path = comment.Path, Side = comment.Side, Line = comment.Line, StartLine = comment.StartLine, Body = comment.Body }],
            };
            return Task.FromResult(commentId);
        }
    }

    public Task UpdatePendingCommentAsync(string commentId, string body, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Pending = Pending! with { Comments = [.. Pending.Comments.Select(c => c.Id == commentId ? c with { Body = body } : c)] };
        }

        return Task.CompletedTask;
    }

    public Task DeletePendingCommentAsync(string commentId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Pending = Pending! with { Comments = [.. Pending.Comments.Where(c => c.Id != commentId)] };
            Threads.RemoveAll(t => t.Comments.Count == 1 && t.Comments[0].Id == commentId);
        }

        return Task.CompletedTask;
    }

    public Task<SubmittedReview> SubmitReviewAsync(string reviewId, ReviewEvent reviewEvent, string body, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Submitted.Add((reviewId, reviewEvent, body));
            Pending = null;
            for (var i = 0; i < Threads.Count; i++)
            {
                Threads[i] = Threads[i] with { Comments = [.. Threads[i].Comments.Select(c => c with { IsPending = false })] };
            }
        }

        return Task.FromResult(new SubmittedReview(reviewId, "https://github.com/acme/billing/pull/42#pullrequestreview-1"));
    }

    public ConcurrentDictionary<string, bool> Viewed { get; } = new(StringComparer.Ordinal);

    public Exception? FailViewedWith { get; set; }

    public Task<IReadOnlySet<string>> GetViewedFilesAsync(PullRequestKey key, CancellationToken cancellationToken) =>
        FailViewedWith is not null
            ? Task.FromException<IReadOnlySet<string>>(FailViewedWith)
            : Task.FromResult<IReadOnlySet<string>>(Viewed.Where(v => v.Value).Select(v => v.Key).ToHashSet(StringComparer.Ordinal));

    public Task SetFileViewedAsync(PullRequestKey key, string path, bool viewed, CancellationToken cancellationToken)
    {
        if (FailViewedWith is not null)
        {
            return Task.FromException(FailViewedWith);
        }

        Viewed[path] = viewed;
        return Task.CompletedTask;
    }
}

internal sealed class FakeWorkspace(IPullRequestCheckout checkout) : IRepositoryWorkspace
{
    public Task<IPullRequestCheckout> CheckoutAsync(
        PullRequestKey key,
        string baseSha,
        string headSha,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("Using test checkout");
        return Task.FromResult(checkout);
    }
}

/// <summary>A directory on disk standing in for a checkout; the "diff" is every file under /new/ as an added file.</summary>
internal sealed class DirectoryCheckout(string root, IReadOnlyDictionary<string, string> addedFiles) : IPullRequestCheckout
{
    public string RootPath => root;

    public string BaseSha => WorkerScenario.BaseSha;

    public string HeadSha => WorkerScenario.HeadSha;

    public string MergeBaseSha => WorkerScenario.BaseSha;

    public Task<string> GetDiffAsync(CancellationToken cancellationToken)
    {
        var diff = new System.Text.StringBuilder();
        foreach (var (path, text) in addedFiles)
        {
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
            diff.Append(System.Globalization.CultureInfo.InvariantCulture, $"diff --git a/{path} b/{path}\nnew file mode 100644\n--- /dev/null\n+++ b/{path}\n@@ -0,0 +1,{lines.Length} @@\n");
            foreach (var line in lines)
            {
                diff.Append('+').Append(line).Append('\n');
            }
        }

        return Task.FromResult(diff.ToString());
    }

    public Task<string?> ReadBaseFileAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(addedFiles.ContainsKey(path) ? null : File.ReadAllText(Path.Combine(root, path)));

    public Task<string?> ReadHeadFileAsync(string path, CancellationToken cancellationToken)
    {
        var full = Path.Combine(root, path);
        return Task.FromResult(File.Exists(full) ? File.ReadAllText(full) : null);
    }
}

/// <summary>The TDD's motivating example: four workers use QueueClaimService, a new one queries the DbContext.</summary>
internal static class WorkerScenario
{
    public const string BaseSha = "1111111111111111111111111111111111111111";
    public const string HeadSha = "2222222222222222222222222222222222222222";
    public const string NewWorkerPath = "src/Workers/InvoiceRetryWorker.cs";
    public const string ExistingWorkerPath = "src/Workers/EmailWorker.cs";

    public static IPullRequestCheckout CreateCheckout(string root)
    {
        var existing = new Dictionary<string, string>
        {
            ["src/Infrastructure/QueueClaimService.cs"] = """
                namespace Acme.Infrastructure;
                public class QueueClaimService { public Task<object[]> ClaimNextAsync(string queue) => Task.FromResult(System.Array.Empty<object>()); }
                public class AppDbContext { }
                """,
        };

        // Unrelated classes, so "takes QueueClaimService" is distinctive to workers rather than universal.
        foreach (var name in new[] { "Invoice", "Customer", "Payment", "Tax", "Pricing", "Audit" })
        {
            existing[$"src/Services/{name}Service.cs"] = $$"""
                namespace Acme.Services;
                public sealed class {{name}}Service(AppDbContext db)
                {
                    public int Count() => 0;
                }
                """;
        }

        foreach (var name in new[] { "Email", "PaymentRetry", "SubscriptionCleanup", "Report" })
        {
            existing[$"src/Workers/{name}Worker.cs"] = $$"""
                namespace Acme.Workers;

                public sealed class {{name}}Worker : BackgroundService
                {
                    private readonly QueueClaimService _claims;

                    public {{name}}Worker(QueueClaimService claims)
                    {
                        _claims = claims;
                    }

                    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                    {
                        var items = await _claims.ClaimNextAsync("{{name}}");
                    }
                }
                """;
        }

        var added = new Dictionary<string, string>
        {
            [NewWorkerPath] = """
                namespace Acme.Workers;

                public sealed class InvoiceRetryWorker : BackgroundService
                {
                    private readonly AppDbContext _context;

                    public InvoiceRetryWorker(AppDbContext context)
                    {
                        _context = context;
                    }

                    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                    {
                        await Task.Delay(1000, stoppingToken);
                    }
                }
                """,
        };

        foreach (var (path, text) in existing.Concat(added))
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        return new DirectoryCheckout(root, added);
    }
}
