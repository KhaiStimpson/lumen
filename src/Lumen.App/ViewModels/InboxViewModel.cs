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
    private readonly TimeProvider _time;
    private CancellationTokenSource? _watch;

    public InboxViewModel(IReviewSource source, IEnumerable<string> recent, Func<string, Task> open, TimeProvider? time = null)
    {
        _source = source;
        _open = open;
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

    public bool IsEmpty => !IsLoading && Sections.All(s => s.Items.Count == 0);

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
