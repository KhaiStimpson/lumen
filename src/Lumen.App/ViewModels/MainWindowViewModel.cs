using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumen.App.Services;
using Lumen.Contracts;
using Lumen.Domain;

namespace Lumen.App.ViewModels;

public enum SettingsSection
{
    Connections,
    Review,
    Files,
    CloudLimits,
    Appearance,
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IReviewSource _source;
    private readonly AppSettings _settings;
    private readonly string _settingsPath;

    public MainWindowViewModel(IReviewSource source, AppSettings settings, string settingsPath)
    {
        _source = source;
        _settings = settings;
        _settingsPath = settingsPath;
        Home = new OpenPullRequestViewModel(settings.RecentPullRequests, OpenAsync);
        Connections = new ConnectionsViewModel(source);
        Review = new ReviewSettingsViewModel(source);
        Appearance = new AppearanceViewModel(settings, settingsPath);
        Review.Saved += (_, _) =>
        {
            if (PullRequest is { } pr)
            {
                pr.NeedsReanalysis = true;
            }
        };
        Current = Home;
    }

    public OpenPullRequestViewModel Home { get; }

    public ConnectionsViewModel Connections { get; }

    public ReviewSettingsViewModel Review { get; }

    public AppearanceViewModel Appearance { get; }

    public (double? Left, double? Right) PaneWidths => (_settings.LeftPaneWidth, _settings.RightPaneWidth);

    public void SavePaneWidths(double left, double right)
    {
        _settings.LeftPaneWidth = left;
        _settings.RightPaneWidth = right;
        _settings.Save(_settingsPath);
    }

    /// <summary>The Settings overlay (docs/design/settings-overlay.md): modal, closed by Escape or a click outside.</summary>
    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    [ObservableProperty]
    public partial SettingsSection Section { get; set; }

    public string SourceDescription => _source.Description;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PullRequest), nameof(IsPullRequestOpen))]
    public partial object Current { get; set; }

    public PullRequestViewModel? PullRequest => Current as PullRequestViewModel;

    public bool IsPullRequestOpen => PullRequest is not null;

    [ObservableProperty]
    public partial string GoToFileQuery { get; set; } = "";

    public async Task OpenAsync(string reference)
    {
        if (!PullRequestKey.TryParse(reference, out var key))
        {
            Home.Error = "Use a GitHub pull request URL or owner/repo#123.";
            return;
        }

        if (PullRequest is { } existing)
        {
            await existing.DisposeAsync().ConfigureAwait(true);
        }

        var canonical = key.ToString();
        _settings.RememberPullRequest(canonical);
        _settings.Save(_settingsPath);
        Home.SetRecent(_settings.RecentPullRequests);
        Home.Error = null;

        var pullRequest = new PullRequestViewModel(_source, new PullRequestRef
        {
            Owner = key.Repository.Owner,
            Name = key.Repository.Name,
            Number = key.Number,
        });
        Current = pullRequest;
        pullRequest.Start();
    }

    [RelayCommand]
    private async Task GoHomeAsync()
    {
        if (PullRequest is { } existing)
        {
            await existing.DisposeAsync().ConfigureAwait(true);
        }

        Current = Home;
    }

    [RelayCommand]
    private Task OpenConnectionsAsync() => OpenSettingsAsync(SettingsSection.Connections);

    [RelayCommand]
    private Task OpenReviewSettingsAsync() => OpenSettingsAsync(SettingsSection.Review);

    /// <summary>Opens on <paramref name="section"/>; review settings load for the open pull request's repository.</summary>
    public Task OpenSettingsAsync(SettingsSection section)
    {
        Section = section;
        IsSettingsOpen = true;
        return Task.WhenAll(Connections.LoadAsync(), Review.LoadAsync(PullRequest?.Ref));
    }

    [RelayCommand]
    private void ShowSection(SettingsSection section) => Section = section;

    [RelayCommand]
    private void CloseSettings()
    {
        IsSettingsOpen = false;
        Connections.KeyInput = "";
    }

    /// <summary>Closes Settings so the fresh analysis is what the reviewer sees next.</summary>
    [RelayCommand]
    private Task ReanalyseAsync()
    {
        IsSettingsOpen = false;
        return PullRequest?.ReanalyseCommand.ExecuteAsync(null) ?? Task.CompletedTask;
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        // Through Appearance, so the Settings overlay and app.json stay in step with the title bar button.
        Appearance.Theme = app.ActualThemeVariant == ThemeVariant.Dark ? ThemeChoice.Light : ThemeChoice.Dark;
    }

    partial void OnGoToFileQueryChanged(string value)
    {
        if (PullRequest is { } pr)
        {
            pr.FileFilter = value;
        }
    }
}

public sealed partial class OpenPullRequestViewModel : ObservableObject
{
    private readonly Func<string, Task> _open;

    public OpenPullRequestViewModel(IEnumerable<string> recent, Func<string, Task> open)
    {
        _open = open;
        SetRecent(recent);
        Reference = Recent.FirstOrDefault() ?? "";
    }

    public ObservableCollection<string> Recent { get; } = [];

    [ObservableProperty]
    public partial string Reference { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public void SetRecent(IEnumerable<string> recent)
    {
        Recent.Clear();
        foreach (var item in recent)
        {
            Recent.Add(item);
        }
    }

    [RelayCommand]
    private Task OpenAsync() => _open(Reference);

    [RelayCommand]
    private Task OpenRecentAsync(string reference)
    {
        Reference = reference;
        return _open(reference);
    }
}
