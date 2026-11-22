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
        var settings = Load("""{"privacy":null,"jev":null,"agents":null,"review":null}""");

        Assert.NotNull(settings.Privacy);
        Assert.NotNull(settings.Jev);
        Assert.NotNull(settings.Agents);
        Assert.NotNull(settings.Review);
        Assert.False(settings.AgentsAllowed);
    }

    [Fact]
    public void HandEditedLimitsAndModelAreMadeUsable()
    {
        var settings = Load("""
            {"privacy":{"allowCloudReasoning":false},
             "jev":{"timeoutSeconds":-10,"model":" "},
             "agents":{"maxConcurrent":100,"maxInvestigationsPerPullRequest":-4}}
            """);

        Assert.False(settings.Privacy.AllowCloudReasoning);
        Assert.Equal(2, settings.Jev.TimeoutSeconds);
        Assert.Equal(new JevSettings().Model, settings.Jev.Model);
        Assert.Equal(4, settings.Agents.MaxConcurrent);
        Assert.Equal(0, settings.Agents.MaxInvestigationsPerPullRequest);
    }

    [Fact]
    public void NullReviewValuesCannotBreakTheSettingsPanelOrAnalysis()
    {
        var settings = Load("""
            {"review":{"sensitivity":null,"ignoredNames":[null," Foo ","Foo"],
             "mechanicalPaths":null,"skippedPaths":null}}
            """);

        Assert.Equal(ReviewSensitivity.Balanced, settings.Review.Sensitivity);
        Assert.Equal(["Foo"], settings.Review.IgnoredNames);
        Assert.Empty(settings.Review.MechanicalPaths);
        Assert.Empty(settings.Review.SkippedPaths);
    }

    [Fact]
    public void InvalidJsonKeepsTheExistingSafeFallback()
    {
        var settings = Load("{");

        Assert.False(settings.AgentsAllowed);
        Assert.Equal(new JevSettings().TimeoutSeconds, settings.Jev.TimeoutSeconds);
    }

    [Fact]
    public void NullEntriesInRepositoryOverridesAreIgnored()
    {
        var engine = new EngineSettingsStore(new EngineSettings(), _directory);
        var store = new ReviewSettingsStore(engine, _directory);
        var repository = new RepositoryRef("acme", "billing");
        var path = store.PathFor(repository);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"ignoredNames":[null," IClock "],"mechanicalPaths":null,"skippedPaths":null}""");

        var resolved = store.Resolve(repository);

        Assert.Equal(["IClock"], resolved.IgnoredNames);
        Assert.Empty(resolved.MechanicalPaths);
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
