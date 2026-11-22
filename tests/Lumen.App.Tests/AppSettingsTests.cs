using Lumen.App.Services;

namespace Lumen.App.Tests;

public sealed class AppSettingsTests
{
    [Theory]
    [InlineData("{\"RecentPullRequests\":null}")]
    [InlineData("{\"RecentPullRequests\":[null,\" \",\"acme/billing#42\",\"acme/billing#42\"]}")]
    public void MalformedRecentListsDoNotPreventOpeningTheApplication(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lumen-app-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            var settings = AppSettings.Load(path);
            Assert.NotNull(settings.RecentPullRequests);
            Assert.All(settings.RecentPullRequests, entry => Assert.False(string.IsNullOrWhiteSpace(entry)));
            Assert.Equal(settings.RecentPullRequests.Count, settings.RecentPullRequests.Distinct().Count());
        }
        finally
        {
            File.Delete(path);
        }
    }

}
