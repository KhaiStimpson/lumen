using Lumen.Domain;
using Lumen.Roslyn;

namespace Lumen.Analysis.Tests;

public sealed class CodeLinkFinderTests
{
    private static readonly Dictionary<string, string> Sources = new()
    {
        ["src/Enrichment/IEnrichmentProvider.cs"] = """
            namespace Crm.Enrichment;
            public interface IEnrichmentProvider { Task<Finding[]> FetchAsync(string company); }
            public sealed record Finding(string Field, string Value);
            """,
        ["src/Enrichment/AbrProvider.cs"] = """
            namespace Crm.Enrichment;
            public sealed class AbrProvider(HttpClient http) : IEnrichmentProvider
            {
                public Task<Finding[]> FetchAsync(string company) => Task.FromResult(System.Array.Empty<Finding>());
            }
            """,
        ["src/Enrichment/Ingestion.cs"] = """
            namespace Crm.Enrichment;
            public sealed class Ingestion
            {
                public Ingestion(IEnumerable<IEnrichmentProvider> providers, Contacts contacts) { }
                public void Save(Finding finding) { }
            }
            """,
        ["src/Data/Contacts.cs"] = """
            namespace Crm.Data;
            public sealed class Contacts { public void Update() { var options = new Options(); } }
            """,
        ["src/Web/ContactPage.cs"] = """
            namespace Crm.Web;
            public sealed class ContactPage { public void Load(Ingestion ingestion) => ingestion.Save(null!); }
            """,
        ["src/Data/Options.cs"] = "namespace Crm.Data; public sealed class Options { }",
        ["src/Other/Options.cs"] = "namespace Crm.Other; public sealed class Options { }",
        ["src/Third/Options.cs"] = "namespace Crm.Third; public sealed class Options { }",
        ["tests/Crm.Tests/IngestionTests.cs"] = """
            namespace Crm.Tests;
            public sealed class IngestionTests
            {
                [Fact] public void Saves() => new Ingestion([new AbrProvider(new HttpClient())], new Contacts()).Save(new Finding("a", "b"));
            }
            """,
    };

    private static IReadOnlyList<FileLink> Links => CodeLinkFinder.Find(Sources);

    private static LinkKind? Kind(string from, string to) =>
        Links.FirstOrDefault(l => l.From.EndsWith(from, StringComparison.Ordinal) && l.To.EndsWith(to, StringComparison.Ordinal))?.Kind;

    [Fact]
    public void AnImplementationLinksToItsInterface()
    {
        Assert.Equal(LinkKind.Implements, Kind("AbrProvider.cs", "IEnrichmentProvider.cs"));
    }

    [Fact]
    public void ConstructorParametersAreDependenciesEvenInsideGenerics()
    {
        Assert.Equal(LinkKind.DependsOn, Kind("Ingestion.cs", "IEnrichmentProvider.cs"));
        Assert.Equal(LinkKind.DependsOn, Kind("Ingestion.cs", "Contacts.cs"));
    }

    [Fact]
    public void ANamedTypeElsewhereIsAReference()
    {
        Assert.Equal(LinkKind.References, Kind("ContactPage.cs", "Ingestion.cs"));

        // One way only: the interface doesn't use what implements or consumes it.
        Assert.Null(Kind("IEnrichmentProvider.cs", "Ingestion.cs"));
        Assert.Null(Kind("IEnrichmentProvider.cs", "AbrProvider.cs"));
    }

    [Fact]
    public void ATestFilesLinksAreWhatItTests()
    {
        Assert.Equal(LinkKind.Tests, Kind("IngestionTests.cs", "Ingestion.cs"));
        Assert.Equal(LinkKind.Tests, Kind("IngestionTests.cs", "AbrProvider.cs"));
        Assert.DoesNotContain(Links, l => l.To.StartsWith("tests/", StringComparison.Ordinal));
    }

    [Fact]
    public void ANameDeclaredInTooManyFilesLinksNothingAndFilesNeverLinkToThemselves()
    {
        Assert.DoesNotContain(Links, l => l.To.EndsWith("Options.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(Links, l => l.From == l.To);
        Assert.Equal(Links.Count, Links.DistinctBy(l => (l.From, l.To)).Count());
    }
}
