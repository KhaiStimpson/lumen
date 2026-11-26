using Lumen.Analysis;
using Lumen.Domain;

namespace Lumen.Engine.Tests;

public sealed class EngineSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lumen-settings-{Guid.NewGuid():N}");

    private EngineSettings Load(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(EngineSettings.PathIn(_directory), json);
        return EngineSettings.Load(_directory);
    }

    [Fact]
    public void NullSettingsSectionsUseSafeDefaults()
    {
        var settings = Load("""{"inbox":null,"review":null}""");

        Assert.NotNull(settings.Inbox);
        Assert.NotNull(settings.Review);
    }

    [Fact]
    public void NullReviewValuesCannotBreakTheSettingsPanelOrAnalysis()
    {
        var settings = Load("""
            {"review":{"mechanicalPaths":[null," src/Gen/** ","src/Gen/**"],"skippedPaths":null}}
            """);

        Assert.Equal(["src/Gen/**"], settings.Review.MechanicalPaths);
        Assert.Empty(settings.Review.SkippedPaths);
    }

    [Fact]
    public void InvalidJsonKeepsTheSafeFallback()
    {
        var settings = Load("{");

        Assert.Empty(settings.Review.MechanicalPaths);
        Assert.Empty(settings.Inbox.WatchedRepositories);
    }

    [Fact]
    public void RemovedSectionsAreIgnoredOnLoad()
    {
        var settings = Load("""{"privacy":{"allowCodeToAgents":true},"jev":{"enabled":true},"review":{"skippedPaths":["x/"]}}""");

        Assert.Equal(["x/"], settings.Review.SkippedPaths);
    }

    [Fact]
    public void NullEntriesInRepositoryOverridesAreIgnored()
    {
        var engine = new EngineSettingsStore(new EngineSettings(), _directory);
        var store = new ReviewSettingsStore(engine, _directory);
        var repository = new RepositoryRef("acme", "billing");
        var path = store.PathFor(repository);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"mechanicalPaths":[null," src/Gen/** "],"skippedPaths":null}""");

        var resolved = store.Resolve(repository);

        Assert.Equal(["src/Gen/**"], resolved.MechanicalPaths);
        Assert.Empty(resolved.SkippedPaths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
