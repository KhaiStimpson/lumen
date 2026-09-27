using Lumen.Agents.ClaudeCode;
using Lumen.Agents.Execution;
using Lumen.Agents.Investigations;
using Lumen.Domain;
using Xunit.Abstractions;

namespace Lumen.Agents.Tests;

/// <summary>
/// Runs one real pattern investigation through the installed, signed-in Claude Code CLI on a tiny synthetic repository.
/// Opt-in (LUMEN_LIVE_CLAUDE=1) because it uses the user's Claude subscription quota; it refuses metered billing.
/// </summary>
public sealed class LiveClaudeCodeTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lumen-live-claude", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task InvestigatesAPatternDeviationOnASubscription()
    {
        if (Environment.GetEnvironmentVariable("LUMEN_LIVE_CLAUDE") != "1")
        {
            return;
        }

        var repo = Path.Combine(_root, "repo");
        foreach (var name in new[] { "Email", "Report", "Payment" })
        {
            Write(repo, $"src/{name}Worker.cs", $$"""
                public sealed class {{name}}Worker(QueueClaimService claims)
                {
                    // Claim first so two instances never process the same item.
                    public async Task RunAsync() => await claims.ClaimNextAsync("{{name}}");
                }
                """);
        }

        Write(repo, "src/InvoiceRetryWorker.cs", """
            public sealed class InvoiceRetryWorker(AppDbContext db)
            {
                public async Task RunAsync() => await db.Invoices.Where(i => i.Failed).ToListAsync();
            }
            """);

        var runner = new SandboxedProcessRunner(new CommandPolicy
        {
            AllowedExecutables = new HashSet<string>(StringComparer.Ordinal) { ClaudeCodeProvider.Executable },
            AllowedWorkingRoots = [_root],
        });
        var provider = new ClaudeCodeProvider(runner, Path.Combine(_root, "state"));
        var auth = await provider.GetAuthenticationStateAsync(CancellationToken.None);
        output.WriteLine($"{auth.Status} · {auth.Billing} · {auth.Detail} · {auth.Version}");
        Assert.Equal(AgentBilling.Subscription, auth.Billing);

        var point = new ReviewPoint
        {
            Id = "pp-live",
            ChangeUnitIds = ["cu-1"],
            Type = ReviewPointType.PatternDeviation,
            Severity = ReviewSeverity.Medium,
            State = ReviewPointState.Visible,
            EvidenceState = EvidenceState.StrongEvidence,
            Title = "Missing QueueClaimService",
            Summary = "InvoiceRetryWorker doesn't take QueueClaimService, unlike 3 of 3 workers in this repository.",
            WhyItMatters = "",
            SuggestedComment = "",
            Anchor = new CodeLocation("src/InvoiceRetryWorker.cs", 1, 1, "InvoiceRetryWorker"),
            Evidence =
            [
                new RepositoryPrecedentEvidence
                {
                    Id = "e1",
                    Source = EvidenceSource.RepositoryPrecedent,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Summary = "3 of 3 workers take QueueClaimService",
                    Producer = "peer-pattern/v1",
                    Convention = "Workers take QueueClaimService",
                    Supporting = 3,
                    PeerCount = 3,
                    Examples = [new PrecedentExample(new CodeLocation("src/EmailWorker.cs", 1, 1, "EmailWorker"), "EmailWorker", "", 1)],
                },
            ],
        };

        var result = await new RepositoryPatternInvestigator(TimeProvider.System)
            .InvestigateAsync(new InvestigationBrief(point, repo, InvestigationBudget.Standard), provider, repo, CancellationToken.None);

        output.WriteLine($"{result.Outcome} · {result.Producer}");
        output.WriteLine(result.Summary);
        foreach (var e in result.Evidence.Concat(result.CounterEvidence))
        {
            output.WriteLine($"{(e.IsCounterEvidence ? "✕" : "✓")} {e.Summary}");
        }

        Assert.NotEqual(InvestigationOutcome.Refuted, result.Outcome);
        Assert.StartsWith("claude-code · pattern-investigator/v1", result.Producer, StringComparison.Ordinal);
    }

    private static void Write(string root, string path, string text)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }
}
