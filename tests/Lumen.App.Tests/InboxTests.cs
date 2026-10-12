using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Lumen.App.Views;
using Lumen.Contracts;

namespace Lumen.App.Tests;

/// <summary>The start screen (plan v2 M1) against the inbox fixture.</summary>
public sealed class InboxTests
{
    private static string ScreensDirectory => Path.Combine(AppContext.BaseDirectory, "screens");

    private static async Task<(MainWindow Window, MainWindowViewModel ViewModel, FixtureReviewSource Source)> ShowAsync()
    {
        var source = Fixture.CreateSource();
        var settingsPath = Path.Combine(Path.GetTempPath(), "lumen-app-tests", $"{Guid.NewGuid():N}.json");
        var viewModel = new MainWindowViewModel(source, new AppSettings { RecentPullRequests = ["KhaiStimpson/andrew-crm#58"] }, settingsPath);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        await Fixture.WaitUntilAsync(() => !viewModel.Home.IsLoading);
        await Fixture.PumpAsync(10);
        return (window, viewModel, source);
    }

    [AvaloniaFact]
    public async Task StartsOnTheInboxWithThreeSectionsAndUnreadMarkers()
    {
        var (window, vm, _) = await ShowAsync();
        try
        {
            Assert.True(vm.IsHome);
            var inbox = vm.Home;
            Assert.Equal(["Review requested", "My open pull requests", "Watched repositories"], inbox.Sections.Select(s => s.Title));
            var top = inbox.Sections[0].Items[0];
            Assert.Equal("KhaiStimpson/andrew-crm#58", top.Reference);
            Assert.True(top.IsUnread);
            Assert.Equal("failure", top.ChecksKind);
            Assert.Equal("Awaiting review", top.ReviewLabel);
            Assert.Equal("+19,700 −120 · 60 files", top.SizeLabel);
            Assert.True(inbox.Sections[1].Items[0].IsDraft);
            Assert.Equal("Changes requested", inbox.Sections[2].Items[1].ReviewLabel);
            Assert.Equal(2, inbox.UnreadCount);

            Assert.True(window.GetVisualDescendants().OfType<InboxView>().Single().IsEffectivelyVisible);
            Directory.CreateDirectory(ScreensDirectory);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(ScreensDirectory, "20-inbox.png"), new PngBitmapEncoderOptions());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task OpeningAnItemOpensThePullRequestAndHomeComesBack()
    {
        var (window, vm, _) = await ShowAsync();
        try
        {
            await vm.Home.OpenItemCommand.ExecuteAsync(vm.Home.Sections[0].Items[0]);
            Assert.NotNull(vm.PullRequest);
            Assert.Equal(58, vm.PullRequest.Ref.Number);
            Assert.False(vm.IsHome);

            await vm.GoHomeCommand.ExecuteAsync(null);
            Assert.True(vm.IsHome);
            Assert.Null(vm.PullRequest);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ComingBackToTheWindowRefreshesFromGitHubAndWatchUpdatesApply()
    {
        var (window, vm, source) = await ShowAsync();
        try
        {
            await vm.OnActivatedAsync();
            Assert.Contains(true, source.InboxRequests);

            var next = (await source.ListInboxAsync(false, CancellationToken.None)).Clone();
            next.Sections[0].Items.RemoveAt(0);
            source.PublishInbox(next);
            await Fixture.WaitUntilAsync(() => vm.Home.Sections[0].Items.Count == 1);
            Assert.Equal(61, vm.Home.Sections[0].Items[0].Model.PullRequest.Number);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PastingAReferenceStillOpensAnything()
    {
        var (window, vm, _) = await ShowAsync();
        try
        {
            vm.Home.Reference = "not a pr";
            await vm.Home.OpenCommand.ExecuteAsync(null);
            Assert.NotNull(vm.Home.Error);
            Assert.True(vm.IsHome);

            vm.Home.Reference = "https://github.com/KhaiStimpson/andrew-crm/pull/58";
            await vm.Home.OpenCommand.ExecuteAsync(null);
            Assert.NotNull(vm.PullRequest);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task WatchedRepositoriesAreEditedInSettings()
    {
        var (window, vm, source) = await ShowAsync();
        try
        {
            await vm.OpenSettingsAsync(SettingsSection.Inbox);
            await Fixture.PumpAsync();
            var settings = vm.InboxSettings;

            settings.NewRepository = "https://github.com/KhaiStimpson/lumen/pull/12";
            await settings.AddCommand.ExecuteAsync(null);
            Assert.Equal(["KhaiStimpson/lumen"], (await source.GetInboxSettingsAsync(CancellationToken.None)).WatchedRepositories);
            Assert.Equal("", settings.NewRepository);

            settings.NewRepository = "nope";
            await settings.AddCommand.ExecuteAsync(null);
            Assert.NotNull(settings.Error);
            Assert.Equal("nope", settings.NewRepository);

            await settings.RemoveCommand.ExecuteAsync("KhaiStimpson/lumen");
            Assert.Empty(settings.WatchedRepositories);
            Assert.True(window.GetVisualDescendants().OfType<InboxSettingsView>().Single().IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void AgesReadNaturally()
    {
        var now = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("just now", InboxItemViewModel.Age(now.AddSeconds(-20), now));
        Assert.Equal("5m ago", InboxItemViewModel.Age(now.AddMinutes(-5), now));
        Assert.Equal("3h ago", InboxItemViewModel.Age(now.AddHours(-3), now));
        Assert.Equal("2d ago", InboxItemViewModel.Age(now.AddDays(-2), now));
    }
}
