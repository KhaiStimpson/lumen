using Lumen.Roslyn;
using static Lumen.Analysis.Tests.Support.TestDiffs;

namespace Lumen.Analysis.Tests;

public sealed class TypeFactsExtractorTests
{
    private const string Worker = """
        using System.Text.Json;
        using Microsoft.Extensions.Logging;

        namespace Acme.Workers;

        [Service]
        [DisallowConcurrentExecutionAttribute]
        public sealed class EmailWorker : BackgroundService, IDisposable
        {
            private readonly QueueClaimService _claims;
            private readonly ILogger<EmailWorker> _logger;

            public EmailWorker(
                QueueClaimService claims,
                ILogger<EmailWorker> logger)
            {
                _claims = claims;
                _logger = logger;
            }

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                var job = await _claims.ClaimNextAsync(stoppingToken);
                _logger.LogInformation("claimed");
                var json = JsonSerializer.Serialize(job);
                this._claims.Release(job);
                if (job is null)
                {
                    throw new InvalidOperationException("nothing");
                }
            }

            [Obsolete]
            public void Dispose()
            {
            }
        }
        """;

    private static TypeFacts Single(string text, string path = "src/Acme/File.cs", bool isTest = false) =>
        Assert.Single(TypeFactsExtractor.Extract(path, text, isTest));

    private static void AssertTrait(TypeFacts facts, string key, int line)
    {
        Assert.True(facts.Traits.TryGetValue(key, out var occurrence), $"Expected trait {key}; had {string.Join(", ", facts.Traits.Keys)}");
        Assert.Equal(line, occurrence.Line);
    }

    [Fact]
    public void ExtractsIdentityAndLocation()
    {
        var facts = Single(Worker, "src/Acme/Workers/EmailWorker.cs");

        Assert.Equal("EmailWorker", facts.Name);
        Assert.Equal("Acme.Workers.EmailWorker", facts.FullName);
        Assert.Equal("src/Acme/Workers/EmailWorker.cs", facts.Path);
        Assert.Equal("Worker", facts.Suffix);
        Assert.Equal(LineOf(Worker, "public sealed class EmailWorker"), facts.DeclarationLine);
        Assert.Equal(LineOf(Worker, "[Service]"), facts.StartLine);
        Assert.Equal(Lines(Worker).Length, facts.EndLine);
        Assert.Equal(LineOf(Worker, "public EmailWorker("), facts.ConstructorLine);
        Assert.False(facts.IsTest);
        Assert.False(facts.IsAbstract);
    }

    [Fact]
    public void ExtractsConstructorDependenciesWithNormalisedGenerics()
    {
        var facts = Single(Worker);

        AssertTrait(facts, "Dependency:QueueClaimService", LineOf(Worker, "QueueClaimService claims,"));
        AssertTrait(facts, "Dependency:ILogger<>", LineOf(Worker, "ILogger<EmailWorker> logger)"));
        Assert.Equal(2, facts.AllTraits.Count(t => t.Kind == TraitKind.Dependency));
    }

    [Fact]
    public void ExtractsBaseTypesAndAttributes()
    {
        var facts = Single(Worker);

        AssertTrait(facts, "BaseType:BackgroundService", LineOf(Worker, "public sealed class EmailWorker"));
        AssertTrait(facts, "BaseType:IDisposable", LineOf(Worker, "public sealed class EmailWorker"));
        AssertTrait(facts, "Attribute:Service", LineOf(Worker, "[Service]"));
        AssertTrait(facts, "Attribute:DisallowConcurrentExecution", LineOf(Worker, "[DisallowConcurrentExecutionAttribute]"));
        AssertTrait(facts, "Attribute:Obsolete", LineOf(Worker, "[Obsolete]"));
        Assert.Equal("concurrency", facts.Traits["Attribute:DisallowConcurrentExecution"].Trait.Category);
    }

    [Fact]
    public void ExtractsThrowsAndCallsThroughFieldsAndStaticTypes()
    {
        var facts = Single(Worker);

        AssertTrait(facts, "Throws:InvalidOperationException", LineOf(Worker, "throw new InvalidOperationException"));
        AssertTrait(facts, "Calls:QueueClaimService.ClaimNextAsync", LineOf(Worker, "_claims.ClaimNextAsync"));
        AssertTrait(facts, "Calls:QueueClaimService.Release", LineOf(Worker, "this._claims.Release"));
        AssertTrait(facts, "Calls:ILogger<>.LogInformation", LineOf(Worker, "_logger.LogInformation"));
        AssertTrait(facts, "Calls:JsonSerializer.Serialize", LineOf(Worker, "JsonSerializer.Serialize"));
        Assert.DoesNotContain(facts.Traits.Keys, k => k.StartsWith("SwallowsExceptions", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractsPrimaryConstructorDependenciesAndCallsThroughParameters()
    {
        const string text = """
            namespace Acme.Providers;

            public sealed class MailgunClient(HttpClient http, ILogger<MailgunClient> logger, Microsoft.Extensions.Options.IOptions<MailgunOptions> options, Dictionary<string, int>? cache) : IEmailClient
            {
                public async Task SendAsync(string body, CancellationToken ct)
                {
                    await http.PostAsync(options.Value.Url, null, ct);
                    logger.LogWarning("sent");
                }
            }
            """;

        var facts = Single(text);
        var line = LineOf(text, "public sealed class MailgunClient(");

        Assert.Equal(line, facts.ConstructorLine);
        AssertTrait(facts, "Dependency:HttpClient", line);
        AssertTrait(facts, "Dependency:ILogger<>", line);
        AssertTrait(facts, "Dependency:IOptions<>", line);
        AssertTrait(facts, "Dependency:Dictionary<,>", line);
        AssertTrait(facts, "BaseType:IEmailClient", line);
        AssertTrait(facts, "Calls:HttpClient.PostAsync", LineOf(text, "http.PostAsync"));
        AssertTrait(facts, "Calls:ILogger<>.LogWarning", LineOf(text, "logger.LogWarning"));
        Assert.Equal("Client", facts.Suffix);
    }

    [Fact]
    public void DistinguishesSwallowingFromRethrowingCatches()
    {
        const string text = """
            namespace Acme;

            public class Rethrows
            {
                public void M()
                {
                    try { Work(); }
                    catch (Exception) { throw; }
                }
            }

            public class Wraps
            {
                public void M()
                {
                    try { Work(); }
                    catch (Exception ex) { throw new ProviderOperationException("failed", ex); }
                }
            }

            public class SwallowsBroadly
            {
                public void M()
                {
                    try { Work(); }
                    catch (System.Exception ex) { Log(ex); }
                }
            }

            public class SwallowsBare
            {
                public void M()
                {
                    try { Work(); }
                    catch { }
                }
            }

            public class SwallowsSpecific
            {
                public void M()
                {
                    try { Work(); }
                    catch (HttpRequestException) { }
                }
            }
            """;

        var types = TypeFactsExtractor.Extract("src/Acme/Errors.cs", text, isTest: false).ToDictionary(t => t.Name);

        Assert.DoesNotContain(types["Rethrows"].Traits.Keys, k => k.StartsWith("SwallowsExceptions", StringComparison.Ordinal));
        Assert.DoesNotContain(types["Wraps"].Traits.Keys, k => k.StartsWith("SwallowsExceptions", StringComparison.Ordinal));
        Assert.True(types["Wraps"].Traits.ContainsKey("Throws:ProviderOperationException"));
        Assert.Equal(LineOf(text, "catch (System.Exception ex)"), types["SwallowsBroadly"].Traits["SwallowsExceptions:Exception"].Line);
        Assert.True(types["SwallowsBare"].Traits.ContainsKey("SwallowsExceptions:Exception"));
        Assert.True(types["SwallowsSpecific"].Traits.ContainsKey("SwallowsExceptions:Specific"));
        Assert.Equal("error-handling", types["SwallowsSpecific"].Traits["SwallowsExceptions:Specific"].Trait.Category);
    }

    [Fact]
    public void ExtractsThrowExpressions()
    {
        const string text = """
            public class Guarded
            {
                private readonly Clock _clock;

                public Guarded(Clock clock)
                {
                    _clock = clock ?? throw new ArgumentNullException(nameof(clock));
                }
            }
            """;

        AssertTrait(Single(text), "Throws:ArgumentNullException", LineOf(text, "?? throw new"));
    }

    [Fact]
    public void NormalisesQualifiedNullableAndArrayTypes()
    {
        const string text = """
            public class Shapes
            {
                public Shapes(global::Acme.Clock clock, Acme.Data.AppDbContext? db, Widget[] widgets, string name)
                {
                }
            }
            """;

        var keys = Single(text).Traits.Keys.Where(k => k.StartsWith("Dependency:", StringComparison.Ordinal)).Order(StringComparer.Ordinal);

        Assert.Equal(["Dependency:AppDbContext", "Dependency:Clock", "Dependency:Widget[]", "Dependency:string"], keys);
    }

    [Fact]
    public void IgnoresCallsOnLocalsAndUnknownLowercaseReceivers()
    {
        const string text = """
            public class Locals
            {
                public void M(Repo repo)
                {
                    var list = new List<int>();
                    list.Add(1);
                    repo.Save();
                }
            }
            """;

        Assert.DoesNotContain(Single(text).Traits.Keys, k => k.StartsWith("Calls:", StringComparison.Ordinal));
    }

    [Fact]
    public void ExcludesNestedStaticAndNonClassTypes()
    {
        const string text = """
            namespace Acme;

            public static class Extensions { public static void X(this int i) { } }

            public abstract class Outer
            {
                private sealed class Inner { }
            }

            public record Money(decimal Amount);

            public interface IThing { }

            public struct Point { }
            """;

        var tree = TypeFactsExtractor.Parse("src/Acme/Mixed.cs", text);
        var facts = Assert.Single(TypeFactsExtractor.Extract(tree, "src/Acme/Mixed.cs", isTest: true));

        Assert.Equal("Outer", facts.Name);
        Assert.True(facts.IsAbstract);
        Assert.True(facts.IsTest);
        Assert.Equal(
            ["Extensions", "IThing", "Inner", "Money", "Outer", "Point"],
            TypeFactsExtractor.DeclaredTypeNames(tree).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FirstOccurrenceOfATraitWins()
    {
        const string text = """
            public class Twice
            {
                public void A() => throw new InvalidOperationException("a");

                public void B() => throw new InvalidOperationException("b");
            }
            """;

        AssertTrait(Single(text), "Throws:InvalidOperationException", LineOf(text, "public void A()"));
    }

    [Fact]
    public void SingleWordNamesHaveNoSuffix()
    {
        Assert.Null(Single("public class Worker { }").Suffix);
        Assert.Equal("Service", Single("public class QueueClaimService { }").Suffix);
    }

    [Fact]
    public void FullNameUsesBlockScopedNamespace()
    {
        Assert.Equal("Acme.Billing.Invoice", Single("namespace Acme.Billing { public class Invoice { } }").FullName);
        Assert.Equal("Invoice", Single("public class Invoice { }").FullName);
    }

    [Fact]
    public void FullNameIncludesAllNestedNamespaces()
    {
        Assert.Equal("Acme.Billing.Invoice", Single("namespace Acme { namespace Billing { public class Invoice { } } }").FullName);
    }
}
