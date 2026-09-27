namespace Lumen.Analysis.Tests.Support;

/// <summary>In-memory C# sources that mirror the product's motivating example.</summary>
internal static class RepoFixtures
{
    public const string QueueClaimServicePath = "src/Acme/Queue/QueueClaimService.cs";
    public const string AppDbContextPath = "src/Acme/Data/AppDbContext.cs";
    public const string ProviderOperationExceptionPath = "src/Acme/Errors/ProviderOperationException.cs";

    public static readonly string[] ExistingWorkers = ["EmailWorker", "PaymentRetryWorker", "SubscriptionCleanupWorker", "ReportWorker"];

    public static readonly string[] ExistingClients = ["MailgunClient", "TwilioClient", "SendGridClient", "SlackClient"];

    public static string WorkerPath(string name, string root = "src/Acme/Workers") => $"{root}/{name}.cs";

    public static string ClientPath(string name) => $"src/Acme/Providers/{name}.cs";

    /// <summary>A worker that claims jobs through QueueClaimService, like the established ones.</summary>
    public static string QueueWorker(string name) => $$"""
        using Acme.Queue;

        namespace Acme.Workers;

        public sealed class {{name}} : BackgroundService
        {
            private readonly QueueClaimService _claims;

            public {{name}}(QueueClaimService claims)
            {
                _claims = claims;
            }

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var job = await _claims.ClaimNextAsync("{{name}}", stoppingToken);
                }
            }
        }
        """;

    /// <summary>A worker that polls the database directly instead of claiming through QueueClaimService.</summary>
    public static string DbWorker(string name, string extraLoopLine = "") => $$"""
        using Acme.Data;

        namespace Acme.Workers;

        public sealed class {{name}} : BackgroundService
        {
            private readonly AppDbContext _db;

            public {{name}}(AppDbContext db)
            {
                _db = db;
            }

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await _db.SaveChangesAsync(stoppingToken);{{extraLoopLine}}
                }
            }
        }
        """;

    public static string ThrowingClient(string name) => $$"""
        using Acme.Errors;

        namespace Acme.Providers;

        public sealed class {{name}}
        {
            private readonly HttpClient _http;

            public {{name}}(HttpClient http)
            {
                _http = http;
            }

            public async Task SendAsync(string payload, CancellationToken ct)
            {
                using var response = await _http.PostAsync("{{name}}", new StringContent(payload), ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw new ProviderOperationException("{{name}} failed");
                }
            }
        }
        """;

    public const string SwallowingClient = """
        using Microsoft.Extensions.Logging;

        namespace Acme.Providers;

        public sealed class StripeClient
        {
            private readonly HttpClient _http;
            private readonly ILogger<StripeClient> _logger;

            public StripeClient(HttpClient http, ILogger<StripeClient> logger)
            {
                _http = http;
                _logger = logger;
            }

            public async Task SendAsync(string payload, CancellationToken ct)
            {
                try
                {
                    using var response = await _http.PostAsync("stripe", new StringContent(payload), ct);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stripe failed");
                }
            }
        }
        """;

    /// <summary>Infrastructure every scenario shares: the repo concepts plus unrelated classes so traits have a realistic base rate.</summary>
    public static Dictionary<string, string> Infrastructure()
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [QueueClaimServicePath] = """
                namespace Acme.Queue;

                public class QueueClaimService
                {
                    public virtual Task<string?> ClaimNextAsync(string queue, CancellationToken ct) => null!;
                }
                """,
            [AppDbContextPath] = """
                namespace Acme.Data;

                public class AppDbContext : DbContext
                {
                }
                """,
            [ProviderOperationExceptionPath] = """
                namespace Acme.Errors;

                public sealed class ProviderOperationException(string message) : Exception(message);
                """,
        };

        for (var i = 0; i < 8; i++)
        {
            sources[$"src/Acme/Misc/Filler{i}.cs"] = $$"""
                namespace Acme.Misc;

                public class Filler{{i}}
                {
                    public int Value { get; set; }
                }
                """;
        }

        return sources;
    }
}
