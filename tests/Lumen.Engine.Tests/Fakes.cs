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
                new PullRequestMetadata("Add invoice retrying", "sarah-chen", "open", false, "main", "feature/retry", "https://github.com/acme/billing/pull/42", null, DateTimeOffset.UtcNow),
                WorkerScenario.BaseSha,
                WorkerScenario.HeadSha));

    public Task<IReadOnlyList<ReviewThread>> GetReviewCommentsAsync(PullRequestKey key, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReviewThread>>([]);

    public Task<PostedComment> PostReviewCommentAsync(NewReviewComment comment, CancellationToken cancellationToken)
    {
        Posted.Add(comment);
        return Task.FromResult(new PostedComment(1001, "https://github.com/acme/billing/pull/42#discussion_r1001"));
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
