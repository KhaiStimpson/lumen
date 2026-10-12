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
    Inbox,
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
        Home = new InboxViewModel(source, settings.RecentPullRequests, OpenAsync);
        InboxSettings = new InboxSettingsViewModel(source);
        InboxSettings.Saved += async (_, _) => await Home.RefreshAsync().ConfigureAwait(true);
        Connections = new ConnectionsViewModel(source);
        Palette = new CommandPaletteViewModel(PaletteSources);
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
        Home.Start();
    }

    public InboxViewModel Home { get; }

    public InboxSettingsViewModel InboxSettings { get; }

    public bool IsHome => Current == Home;

    /// <summary>The window came back to the foreground: refresh what's on screen (plan v2 M1: polled, and on focus).</summary>
    public Task OnActivatedAsync() => IsHome ? Home.RefreshAsync() : Task.CompletedTask;

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
    [NotifyPropertyChangedFor(nameof(PullRequest), nameof(IsPullRequestOpen), nameof(IsHome))]
    public partial object Current { get; set; }

    partial void OnCurrentChanged(object value)
    {
        // The inbox polls only while it is on screen.
        if (value == Home)
        {
            Home.Start();
        }
        else
        {
            Home.Stop();
        }
    }

    public PullRequestViewModel? PullRequest => Current as PullRequestViewModel;

    public bool IsPullRequestOpen => PullRequest is not null;

    /// <summary>Ctrl+K (plan v2 M6). Its input is the title bar's search box.</summary>
    public CommandPaletteViewModel Palette { get; }

    /// <summary>Asks the view to open a link (e.g. "Open on GitHub").</summary>
    public event EventHandler<string>? OpenUrlRequested;

    private List<PaletteItemViewModel> PaletteSources()
    {
        var pr = PullRequest;
        var items = new List<PaletteItemViewModel>();
        PaletteItemViewModel Action(string title, string shortcut, Func<Task> run, string detail = "") =>
            new(PaletteItemKind.Action, title, detail, shortcut, run);
        Task Do(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        if (pr is not null)
        {
            foreach (var file in (pr.HasScope ? pr.ScopeFiles : pr.Files))
            {
                items.Add(new PaletteItemViewModel(PaletteItemKind.File, file.Name, file.Path, "", async () =>
                {
                    pr.ShowTab(PullRequestTab.Files);
                    await pr.SelectFileAsync(file).ConfigureAwait(true);
                }));
            }

            items.Add(Action("Next review point", "J", pr.NextPointAsync));
            items.Add(Action("Previous review point", "K", pr.PreviousPointAsync));
            items.Add(Action("Examine the current point", "Enter", () => pr.ExamineAsync(null)));
            items.Add(Action("Next open thread", "N", pr.NextThreadAsync));
            items.Add(Action("Previous open thread", "Shift+N", pr.PreviousThreadAsync));
            items.Add(Action("Open the review tray", "R", () => Do(() => pr.IsTrayOpen = true)));
            items.Add(Action("Submit review: Comment", "", pr.SubmitCommentAsync));
            if (pr.CanApprove)
            {
                items.Add(Action("Submit review: Approve", "", pr.ApproveAsync));
                items.Add(Action("Submit review: Request changes", "", pr.RequestChangesAsync));
            }

            items.Add(Action(pr.IsSplitView ? "Unified view" : "Split view (side by side)", "S", () => Do(pr.ToggleSplitView)));
            items.Add(Action(pr.IgnoreWhitespace ? "Show whitespace changes" : "Ignore whitespace", "W", () => Do(pr.ToggleIgnoreWhitespace)));
            if (pr.CanShowSinceLastReview)
            {
                items.Add(Action(pr.ShowSinceLastReview ? "Show all changes" : "Since last review", "", () => Do(() => pr.ShowSinceLastReview = !pr.ShowSinceLastReview), pr.LastReviewedLabel));
            }

            items.Add(Action(pr.ShowFullFile ? "Show only the changes" : "Show the full file", "X", () => Do(() => pr.ShowFullFile = !pr.ShowFullFile)));
            items.Add(Action("Find in file", "Ctrl+F", () => Do(pr.OpenFind)));
            items.Add(Action("Files", "", () => Do(() => pr.ShowTab(PullRequestTab.Files))));
            items.Add(Action("Conversation", "", () => Do(() => pr.ShowTab(PullRequestTab.Conversation))));
            items.Add(Action("Commits", "", () => Do(() => pr.ShowTab(PullRequestTab.Commits))));
            items.Add(Action("Checks", "", () => Do(() => pr.ShowTab(PullRequestTab.Checks))));
            if (pr.WalksGroups || pr.Walkthrough is { Groups.Count: > 0 })
            {
                items.Add(Action("Next change group", "", pr.NextGroupAsync));
            }

            items.Add(Action("Run the walkthrough again", "", pr.RefreshWalkthroughAsync));
            if (pr.Url.Length > 0)
            {
                items.Add(Action("Open on GitHub", "", () => Do(() => OpenUrlRequested?.Invoke(this, pr.Url))));
            }

            items.Add(Action("Re-analyse", "", () => pr.RefreshCommand.ExecuteAsync(null)));
            items.Add(Action("Inbox", "", () => GoHomeCommand.ExecuteAsync(null)));
        }
        else
        {
            items.Add(Action("Refresh the inbox", "", Home.RefreshAsync));
        }

        foreach (var item in Home.AllItems)
        {
            items.Add(new PaletteItemViewModel(PaletteItemKind.PullRequest, $"{item.NumberLabel} {item.Title}", item.Reference, "", () => OpenAsync(item.Reference)));
        }

        foreach (var recent in _settings.RecentPullRequests.Where(r => Home.AllItems.All(i => !i.Reference.Equals(r, StringComparison.OrdinalIgnoreCase))))
        {
            items.Add(new PaletteItemViewModel(PaletteItemKind.PullRequest, recent, "Recent", "", () => OpenAsync(recent)));
        }

        items.Add(Action("Connections", "", OpenConnectionsAsync));
        items.Add(Action("Settings: Inbox", "", () => OpenSettingsAsync(SettingsSection.Inbox)));
        items.Add(Action("Settings: Review", "", OpenReviewSettingsAsync));
        items.Add(Action("Toggle light / dark", "", () => Do(ToggleTheme)));
        return items;
    }

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
        pullRequest.IsSplitView = _settings.SplitView;
        pullRequest.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PullRequestViewModel.IsSplitView) && _settings.SplitView != pullRequest.IsSplitView)
            {
                _settings.SplitView = pullRequest.IsSplitView;
                _settings.Save(_settingsPath);
            }
        };
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

    [RelayCommand]
    private void OpenPalette() => Palette.Open();

    /// <summary>Opens on <paramref name="section"/>; review settings load for the open pull request's repository.</summary>
    public Task OpenSettingsAsync(SettingsSection section)
    {
        Section = section;
        IsSettingsOpen = true;
        return Task.WhenAll(Connections.LoadAsync(), Review.LoadAsync(PullRequest?.Ref), InboxSettings.LoadAsync());
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

}
