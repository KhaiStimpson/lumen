using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Lumen.App.Services;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

/// <summary>
/// The start screen (plan v2 M1): pull requests waiting on the viewer, their own, and the ones in watched
/// repositories. Pasting a URL or <c>owner/repo#n</c> still opens anything else.
/// </summary>
public sealed partial class InboxViewModel : ObservableObject, IDisposable
{
    private readonly IReviewSource _source;
    private readonly Func<string, Task> _open;
    private readonly Func<LocalBranchRef, Task>? _openLocal;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _watch;

    public InboxViewModel(IReviewSource source, IEnumerable<string> recent, Func<string, Task> open, TimeProvider? time = null, Func<LocalBranchRef, Task>? openLocal = null)
    {
        _source = source;
        _open = open;
        _openLocal = openLocal;
        _time = time ?? TimeProvider.System;
        SetRecent(recent);
        Reference = "";
    }

    // Open by reference -----------------------------------------------------------------------------

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

    // Local branches -------------------------------------------------------------------------------

    /// <summary>Local clones reviewed before, newest first (docs/overhaul.md: the inbox's Local branches section).</summary>
    public ObservableCollection<LocalBranchItemViewModel> LocalBranches { get; } = [];

    public bool HasLocalBranches => LocalBranches.Count > 0;

    public string LocalCountLabel => LocalBranches.Count == 0 ? "" : LocalBranches.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The "Open local branch…" form.</summary>
    [ObservableProperty]
    public partial bool IsLocalFormOpen { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenLocalCommand))]
    public partial string LocalPath { get; set; } = "";

    /// <summary>Empty: the clone's default base (origin/HEAD, main, master or develop).</summary>
    [ObservableProperty]
    public partial string LocalBase { get; set; } = "";

    /// <summary>Empty: the working tree, uncommitted changes included. Otherwise a branch.</summary>
    [ObservableProperty]
    public partial string LocalHead { get; set; } = "";

    [RelayCommand]
    private void ToggleLocalForm() => IsLocalFormOpen = !IsLocalFormOpen;

    [RelayCommand(CanExecute = nameof(CanOpenLocal))]
    private Task OpenLocalAsync() =>
        _openLocal?.Invoke(LocalRef(LocalPath, LocalHead, LocalBase)) ?? Task.CompletedTask;

    private bool CanOpenLocal() => LocalPath.Trim().Length > 0;

    [RelayCommand]
    private static Task OpenLocalItemAsync(LocalBranchItemViewModel? item) => item?.OpenAsync() ?? Task.CompletedTask;

    public static LocalBranchRef LocalRef(string path, string head, string baseRef) => new()
    {
        ClonePath = path.Trim(),
        WorkingTree = head.Trim().Length == 0,
        HeadRef = head.Trim(),
        BaseRef = baseRef.Trim(),
    };

    public async Task LoadLocalBranchesAsync()
    {
        try
        {
            var reply = await _source.ListLocalBranchesAsync(CancellationToken.None).ConfigureAwait(true);
            var now = _time.GetUtcNow();
            LocalBranches.ReplaceWith(reply.Items.Select(i => new LocalBranchItemViewModel(i, r => _openLocal?.Invoke(r) ?? Task.CompletedTask, now)));
        }
        catch (RpcException)
        {
            // The section stays empty; the inbox itself still works.
        }
        catch (EngineUnavailableException)
        {
        }

        OnPropertyChanged(nameof(HasLocalBranches));
        OnPropertyChanged(nameof(LocalCountLabel));
        OnPropertyChanged(nameof(IsEmpty));
    }

    // Inbox ----------------------------------------------------------------------------------------

    public ObservableCollection<InboxSectionViewModel> Sections { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; private set; } = true;

    /// <summary>GitHub couldn't be reached; the sections keep the last good result.</summary>
    [ObservableProperty]
    public partial string? LoadError { get; private set; }

    [ObservableProperty]
    public partial bool IsAuthenticationError { get; private set; }

    [ObservableProperty]
    public partial string ViewerLogin { get; private set; } = "";

    [ObservableProperty]
    public partial string UpdatedLabel { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsRefreshing { get; private set; }

    public bool IsEmpty => !IsLoading && Sections.All(s => s.Items.Count == 0) && LocalBranches.Count == 0;

    public IEnumerable<InboxItemViewModel> AllItems => Sections.SelectMany(s => s.Items);

    public int UnreadCount => AllItems.Count(i => i.IsUnread);

    /// <summary>Follows the engine's inbox (polled every two minutes) while the start screen is showing.</summary>
    public void Start()
    {
        if (_watch is not null)
        {
            return;
        }

        _watch = new CancellationTokenSource();
        _ = WatchAsync(_watch.Token);
        _ = LoadLocalBranchesAsync();
    }

    public void Stop()
    {
        _watch?.Cancel();
        _watch?.Dispose();
        _watch = null;
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var inbox in _source.WatchInboxAsync(cancellationToken).ConfigureAwait(true))
            {
                Apply(inbox);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
        }
        catch (RpcException ex)
        {
            Fail(ex.Status.Detail);
        }
        catch (EngineUnavailableException ex)
        {
            Fail(ex.Message);
        }
    }

    /// <summary>Asks GitHub now (the Refresh button, and when the window regains focus).</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            Apply(await _source.ListInboxAsync(refresh: true, CancellationToken.None).ConfigureAwait(true));
            await LoadLocalBranchesAsync().ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            Fail(ex.Status.Detail);
        }
        catch (EngineUnavailableException ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private Task OpenItemAsync(InboxItemViewModel? item) => item is null ? Task.CompletedTask : _open(item.Reference);

    private void Fail(string message)
    {
        LoadError = message;
        IsLoading = false;
    }

    internal void Apply(Inbox inbox)
    {
        LoadError = inbox.Error.Length > 0 ? inbox.Error : null;
        IsAuthenticationError = inbox.IsAuthenticationError;
        if (inbox.ViewerLogin.Length > 0)
        {
            ViewerLogin = inbox.ViewerLogin;
        }

        var now = _time.GetUtcNow();
        if (inbox.Sections.Count > 0)
        {
            Sections.Clear();
            foreach (var section in inbox.Sections)
            {
                Sections.Add(new InboxSectionViewModel(section, now));
            }
        }

        UpdatedLabel = inbox.FetchedAt is { } fetched ? $"Updated {InboxItemViewModel.Age(fetched.ToDateTimeOffset(), now)}" : "";
        IsLoading = false;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(AllItems));
        OnPropertyChanged(nameof(UnreadCount));
    }

    public void Dispose() => Stop();
}

public sealed class InboxSectionViewModel(InboxSection section, DateTimeOffset now)
{
    public string Id => section.Id;

    public string Title => section.Title;

    public string Detail => section.Detail;

    public IReadOnlyList<InboxItemViewModel> Items { get; } = [.. section.Items.Select(i => new InboxItemViewModel(i, now))];

    public string CountLabel => Items.Count == 0 ? "" : Items.Count.ToString(CultureInfo.InvariantCulture);

    public bool IsEmpty => Items.Count == 0;
}

public sealed class InboxItemViewModel(InboxItem item, DateTimeOffset now)
{
    public InboxItem Model { get; } = item;

    public string Reference => $"{Model.PullRequest.Owner}/{Model.PullRequest.Name}#{Model.PullRequest.Number}";

    public string RepositoryLabel => $"{Model.PullRequest.Owner}/{Model.PullRequest.Name}";

    public string NumberLabel => $"#{Model.PullRequest.Number}";

    public string Title => Model.Title;

    public string Author => Model.Author;

    public string AgeLabel => Age(Model.UpdatedAt?.ToDateTimeOffset() ?? now, now);

    public ReviewProgress Progress => Model.Progress ?? new ReviewProgress { ChangedLines = Model.Additions + Model.Deletions };

    /// <summary>"4,212 changed" alone until the change has been analysed, then "4,212 changed → 1,180 to read".</summary>
    public string ShrinkLabel => ProgressText.Shrink(Progress);

    /// <summary>The head moved since the analysis, so the to-read figure is out of date.</summary>
    public bool IsOutOfDate => Progress is { Analysed: true, OutOfDate: true };

    /// <summary>"3 of 8 slices reviewed"; empty until analysed.</summary>
    public string SlicesLabel => ProgressText.Slices(Progress);

    public bool HasSlices => SlicesLabel.Length > 0;

    public string SizeLabel => string.Create(
        CultureInfo.InvariantCulture,
        $"+{Model.Additions:N0} −{Model.Deletions:N0} · {Model.ChangedFiles} file{(Model.ChangedFiles == 1 ? "" : "s")}");

    public bool IsDraft => Model.IsDraft;

    public bool IsUnread => Model.Unread;

    public bool IsNew => Model.IsNew;

    /// <summary>"success", "failure", "pending" or "" (none), for the check glyph.</summary>
    public string ChecksKind => Model.Checks switch
    {
        InboxCheckState.Success => "success",
        InboxCheckState.Failure => "failure",
        InboxCheckState.Pending => "pending",
        _ => "",
    };

    public string ChecksLabel => Model.Checks switch
    {
        InboxCheckState.Success => "Checks passed",
        InboxCheckState.Failure => "Checks failing",
        InboxCheckState.Pending => "Checks running",
        _ => "No checks",
    };

    public bool HasChecks => Model.Checks != InboxCheckState.None;

    public string ReviewLabel => Model.Review switch
    {
        InboxReviewState.Approved => "Approved",
        InboxReviewState.ChangesRequested => "Changes requested",
        InboxReviewState.Awaiting => "Awaiting review",
        _ => "",
    };

    /// <summary>"approved", "changes" or "awaiting", for the pill colour.</summary>
    public string ReviewKind => Model.Review switch
    {
        InboxReviewState.Approved => "approved",
        InboxReviewState.ChangesRequested => "changes",
        InboxReviewState.Awaiting => "awaiting",
        _ => "",
    };

    public bool HasReviewState => ReviewLabel.Length > 0;

    /// <summary>"now", "5m", "3h", "2d", "Oct 3".</summary>
    public static string Age(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalMinutes}m ago")
            : age.TotalDays < 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalHours}h ago")
            : age.TotalDays < 30 ? string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalDays}d ago")
            : at.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture);
    }
}

/// <summary>The inbox's progress figures in words, shared by pull requests and local branches.</summary>
public static class ProgressText
{
    public static string Shrink(ReviewProgress progress) => progress.Analysed
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{progress.ChangedLines:N0} changed → {progress.ToReadLines:N0} to read")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{progress.ChangedLines:N0} changed");

    public static string Slices(ReviewProgress progress) => progress is { Analysed: true, SlicesTotal: > 0 }
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(progress.SlicesReviewed == progress.SlicesTotal ? "✓ " : "")}{progress.SlicesReviewed} of {progress.SlicesTotal} slices reviewed")
        : "";
}
