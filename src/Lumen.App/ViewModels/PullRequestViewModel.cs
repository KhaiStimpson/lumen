using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Lumen.App.Diff;
using Lumen.App.Services;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

public enum ReviewMode
{
    Diff,
    Examine,
}

public enum NavigationReason
{
    Next,
    Previous,
    Select,
    Arrived,
    Refocus,
}

/// <summary>Asks the view to bring a review point into view (the view owns the motion).</summary>
public sealed record NavigationRequest(ReviewPointViewModel Point, NavigationReason Reason, ReviewPointViewModel? From);

/// <summary>One open pull request: its files, diff, review points and the guided-review state (TDD §20).</summary>
public sealed partial class PullRequestViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IReviewSource _source;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, FileDiff> _diffCache = new(StringComparer.Ordinal);

    // Head-side file text for the full-file view; null when the file has none (deleted) or couldn't be loaded.
    private readonly Dictionary<string, string?> _headCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileTreeNode> _nodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _arrived = new(StringComparer.Ordinal);
    private Task? _watch;
    private int _diffVersion;

    public PullRequestViewModel(IReviewSource source, PullRequestRef pullRequest)
    {
        _source = source;
        Ref = pullRequest;
        Title = $"{pullRequest.Owner}/{pullRequest.Name} #{pullRequest.Number}";
        Status = "Connecting…";
    }

    public PullRequestRef Ref { get; }

    public string NumberLabel => $"#{Ref.Number}";

    public string RepositoryLabel => $"{Ref.Owner} / {Ref.Name}";

    public event EventHandler<NavigationRequest>? NavigationRequested;

    // Header ---------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Author { get; set; } = "";

    [ObservableProperty]
    public partial string StateLabel { get; set; } = "";

    [ObservableProperty]
    public partial string Url { get; set; } = "";

    [ObservableProperty]
    public partial string BranchLabel { get; set; } = "";

    [ObservableProperty]
    public partial int Additions { get; set; }

    [ObservableProperty]
    public partial int Deletions { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; }

    private string _completeStatus = "";

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial bool IsAnalysing { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool IsAuthenticationError { get; set; }

    [ObservableProperty]
    public partial string ViewerLogin { get; set; } = "";

    public bool ViewerIsAuthor => ViewerLogin.Length > 0 && string.Equals(ViewerLogin, Author, StringComparison.OrdinalIgnoreCase);

    // Triage ---------------------------------------------------------------------------------------

    /// <summary>Where to spend attention, per hunk; null until the engine sends it (or when it could not compute it).</summary>
    [ObservableProperty]
    public partial TriageReady? Triage { get; set; }

    // Files ----------------------------------------------------------------------------------------

    public ObservableCollection<FileEntryViewModel> Files { get; } = [];

    public ObservableCollection<FileTreeNode> Tree { get; } = [];

    public ObservableCollection<FileEntryViewModel> GeneratedFiles { get; } = [];

    [ObservableProperty]
    public partial bool ShowGenerated { get; set; }

    [ObservableProperty]
    public partial string FileFilter { get; set; } = "";

    [ObservableProperty]
    public partial FileEntryViewModel? SelectedFile { get; set; }

    [ObservableProperty]
    public partial FileTreeNode? SelectedNode { get; set; }

    [ObservableProperty]
    public partial DiffDocument? CurrentDiff { get; set; }

    [ObservableProperty]
    public partial bool IsDiffLoading { get; set; }

    /// <summary>Show the whole file with the changes in place, instead of only the hunks.</summary>
    [ObservableProperty]
    public partial bool ShowFullFile { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<GutterMarker> Markers { get; set; } = [];

    [ObservableProperty]
    public partial (int Start, int End)? FocusRange { get; set; }

    public string ViewedSummary => string.Create(CultureInfo.InvariantCulture, $"{Files.Count(f => f.IsViewed)} / {Files.Count} viewed");

    public string GeneratedSummary => GeneratedFiles.Count == 0
        ? ""
        : string.Create(CultureInfo.InvariantCulture, $"{GeneratedFiles.Count} generated files · +{GeneratedFiles.Sum(f => f.Model.Additions)}");

    // Review points --------------------------------------------------------------------------------

    public ObservableCollection<ReviewPointViewModel> ReviewPoints { get; } = [];

    public ObservableCollection<ConventionViewModel> Conventions { get; } = [];

    public IEnumerable<ReviewPointViewModel> OpenPoints => ReviewPoints.Where(p => !p.IsDismissed);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionLabel), nameof(HasCurrentPoint))]
    public partial ReviewPointViewModel? CurrentPoint { get; set; }

    public bool HasCurrentPoint => CurrentPoint is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExamining))]
    public partial ReviewMode Mode { get; set; }

    public bool IsExamining => Mode == ReviewMode.Examine;

    /// <summary>"precedent", "evidence" or "comment".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrecedentTab), nameof(IsEvidenceTab), nameof(IsCommentTab))]
    public partial string ExamineTab { get; set; } = "precedent";

    public bool IsPrecedentTab
    {
        get => ExamineTab == "precedent";
        set
        {
            if (value)
            {
                ExamineTab = "precedent";
            }
        }
    }

    public bool IsEvidenceTab
    {
        get => ExamineTab == "evidence";
        set
        {
            if (value)
            {
                ExamineTab = "evidence";
            }
        }
    }

    public bool IsCommentTab
    {
        get => ExamineTab == "comment";
        set
        {
            if (value)
            {
                ExamineTab = "comment";
                StartComment(null);
            }
        }
    }

    public string PositionLabel
    {
        get
        {
            var open = OpenPoints.ToList();
            var index = CurrentPoint is null ? -1 : open.IndexOf(CurrentPoint);
            return open.Count == 0 ? (IsAnalysing ? "—" : "none open") :
                index < 0 ? string.Create(CultureInfo.InvariantCulture, $"{open.Count} review points") :
                string.Create(CultureInfo.InvariantCulture, $"{index + 1} of {open.Count}");
        }
    }

    public string ReviewFocusLabel => string.Create(CultureInfo.InvariantCulture, $"{OpenPoints.Count()} key points");

    [ObservableProperty]
    public partial string? Toast { get; set; }

    // Lifecycle ------------------------------------------------------------------------------------

    public void Start() => _watch ??= WatchAsync(refresh: false);

    /// <summary>A review setting changed since this pull request was analysed; offer Re-analyse, never run it unasked.</summary>
    [ObservableProperty]
    public partial bool NeedsReanalysis { get; set; }

    public string ReanalyseLabel => $"Re-analyse {NumberLabel}";

    [RelayCommand]
    private Task ReanalyseAsync()
    {
        NeedsReanalysis = false;
        return RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _watchCts.CancelAsync().ConfigureAwait(true);
        if (_watch is not null)
        {
            try
            {
                await _watch.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _watch = null;
        ResetForReload();
        _watch = WatchAsync(refresh: true);
    }

    private CancellationTokenSource _watchCts = new();

    private void ResetForReload()
    {
        _watchCts = new CancellationTokenSource();
        ReviewPoints.Clear();
        Conventions.Clear();
        _arrived.Clear();
        _diffCache.Clear();
        _headCache.Clear();
        CurrentPoint = null;
        Mode = ReviewMode.Diff;
        IsAnalysing = true;
        Error = null;
    }

    private async Task WatchAsync(bool refresh)
    {
        var token = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _watchCts.Token).Token;
        try
        {
            await foreach (var evt in _source.WatchAsync(Ref, refresh, token).ConfigureAwait(true))
            {
                await ApplyAsync(evt).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && token.IsCancellationRequested)
        {
            // gRPC reports our own cancellation (Re-analyse, Retry, closing the PR) this way, not as OperationCanceledException.
        }
        catch (RpcException ex)
        {
            Fail(ex.Status.Detail, isAuthentication: false);
        }
        catch (EngineUnavailableException ex)
        {
            Fail(ex.Message, isAuthentication: false);
        }
    }

    private void Fail(string message, bool isAuthentication)
    {
        Error = message;
        IsAuthenticationError = isAuthentication;
        IsAnalysing = false;
        IsLoading = false;
        Status = "Stopped";
    }

    internal async Task ApplyAsync(PullRequestEvent evt)
    {
        switch (evt.EventCase)
        {
            case PullRequestEvent.EventOneofCase.Progress:
                Status = evt.Progress.Message;
                break;

            case PullRequestEvent.EventOneofCase.Snapshot:
                await ApplySnapshotAsync(evt.Snapshot).ConfigureAwait(true);
                break;

            case PullRequestEvent.EventOneofCase.TriageReady:
                Triage = evt.TriageReady;
                break;

            case PullRequestEvent.EventOneofCase.ReviewPointAdded:
                AddReviewPoint(evt.ReviewPointAdded);
                break;

            case PullRequestEvent.EventOneofCase.ReviewPointStateChanged:
                if (ReviewPoints.FirstOrDefault(p => p.Id == evt.ReviewPointStateChanged.ReviewPointId) is { } changed)
                {
                    changed.State = evt.ReviewPointStateChanged.State;
                    RefreshPointDerivedState();
                }

                break;

            case PullRequestEvent.EventOneofCase.FileViewed:
                if (Files.FirstOrDefault(f => f.Path == evt.FileViewed.Path) is { } viewedFile)
                {
                    viewedFile.IsViewed = evt.FileViewed.Viewed;
                    OnPropertyChanged(nameof(ViewedSummary));
                }

                break;

            case PullRequestEvent.EventOneofCase.Conventions:
                Conventions.Clear();
                foreach (var convention in RankConventions(evt.Conventions.Conventions).Take(5))
                {
                    Conventions.Add(new ConventionViewModel(convention));
                }

                break;

            case PullRequestEvent.EventOneofCase.ReviewPointUpdated:
                if (ReviewPoints.FirstOrDefault(p => p.Id == evt.ReviewPointUpdated.Id) is { } existing)
                {
                    existing.Update(evt.ReviewPointUpdated);
                    RefreshPointDerivedState();
                }

                break;

            case PullRequestEvent.EventOneofCase.InvestigationStatus:
                // TDD §49: one quiet line, never an agents dashboard.
                var active = evt.InvestigationStatus.Active;
                Status = active > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"Analysing {active} area{(active == 1 ? "" : "s")}…")
                    : evt.InvestigationStatus.Detail.Length > 0 ? $"{_completeStatus} · {evt.InvestigationStatus.Detail}" : _completeStatus;
                break;

            case PullRequestEvent.EventOneofCase.Complete:
                IsAnalysing = false;
                OnPropertyChanged(nameof(PositionLabel));
                _completeStatus = OpenPoints.Any()
                    ? string.Create(CultureInfo.InvariantCulture, $"{OpenPoints.Count()} review points · analysed in {evt.Complete.DurationMs / 1000.0:0.0}s")
                    : "Nothing needs special attention";
                Status = _completeStatus;
                if (CurrentPoint is null && OpenPoints.FirstOrDefault() is { } first)
                {
                    await GoToAsync(first, NavigationReason.Arrived).ConfigureAwait(true);
                }

                break;

            case PullRequestEvent.EventOneofCase.Failed:
                Fail(evt.Failed.Message, evt.Failed.IsAuthentication);
                break;
        }
    }

    /// <summary>Conventions behind the surfaced review points first, then the broadest well-supported ones.</summary>
    private IEnumerable<Convention> RankConventions(IEnumerable<Convention> conventions)
    {
        var groups = ReviewPoints
            .SelectMany(p => p.Model.Evidence)
            .Where(e => !e.IsCounterEvidence && e.Convention.Length > 0)
            .Select(e => e.Convention)
            .ToList();
        return conventions
            .OrderByDescending(c => groups.Any(g => g.StartsWith(c.PeerGroup, StringComparison.Ordinal)))
            .ThenByDescending(c => c.PeerCount)
            .ThenByDescending(c => (double)c.Supporting / Math.Max(1, c.PeerCount));
    }

    private async Task ApplySnapshotAsync(Contracts.PullRequestSnapshot snapshot)
    {
        Title = snapshot.Title;
        Author = snapshot.Author;
        ViewerLogin = snapshot.ViewerLogin;
        StateLabel = snapshot.IsDraft ? "Draft" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(snapshot.State);
        Url = snapshot.Url;
        BranchLabel = $"{snapshot.HeadRef} → {snapshot.BaseRef}";
        Additions = snapshot.Files.Sum(f => f.Additions);
        Deletions = snapshot.Files.Sum(f => f.Deletions);
        OnPropertyChanged(nameof(ViewerIsAuthor));

        Files.Clear();
        GeneratedFiles.Clear();
        Tree.Clear();
        _nodes.Clear();
        foreach (var file in snapshot.Files)
        {
            var entry = new FileEntryViewModel(file);
            Files.Add(entry);
            if (entry.IsMechanical)
            {
                GeneratedFiles.Add(entry);
            }
        }

        RebuildTree();
        OnPropertyChanged(nameof(GeneratedSummary));
        OnPropertyChanged(nameof(ViewedSummary));
        IsLoading = false;

        var firstReviewable = Tree.SelectMany(n => n.Descendants().Prepend(n)).FirstOrDefault(n => n.File is not null)?.File ?? Files.FirstOrDefault();
        if (SelectedFile is null && firstReviewable is not null)
        {
            await SelectFileAsync(firstReviewable).ConfigureAwait(true);
        }
    }

    private void RebuildTree()
    {
        Tree.Clear();
        _nodes.Clear();
        var filter = FileFilter.Trim();
        var visible = Files.Where(f => !f.IsMechanical && (filter.Length == 0 || f.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        foreach (var node in FileTreeNode.Build(visible))
        {
            Tree.Add(node);
        }

        foreach (var node in Tree.SelectMany(n => n.Descendants().Prepend(n)).Where(n => n.File is not null))
        {
            _nodes[node.Path] = node;
        }

        if (SelectedFile is not null)
        {
            SelectedNode = _nodes.GetValueOrDefault(SelectedFile.Path);
        }
    }

    partial void OnFileFilterChanged(string value) => RebuildTree();

    partial void OnSelectedNodeChanged(FileTreeNode? value)
    {
        if (value?.File is { } file && file != SelectedFile)
        {
            _ = SelectFileAsync(file);
        }
    }

    private void AddReviewPoint(ReviewPoint model)
    {
        var point = new ReviewPointViewModel(model, ReviewPoints.Count + 1);
        ReviewPoints.Add(point);
        _arrived.Add(point.Id);

        // Keep the list ranked: severity, then priority (the engine's order), stable for equal ranks.
        var ranked = ReviewPoints.OrderByDescending(p => p.Model.Severity).ThenByDescending(p => p.Model.Priority).ToList();
        for (var i = 0; i < ranked.Count; i++)
        {
            var current = ReviewPoints.IndexOf(ranked[i]);
            if (current != i)
            {
                ReviewPoints.Move(current, i);
            }

            ranked[i].Ordinal = i + 1;
        }

        foreach (var location in point.Locations)
        {
            if (Files.FirstOrDefault(f => f.Path == location.Path) is { } file)
            {
                file.ReviewPointCount++;
            }
        }

        RefreshPointDerivedState();
        if (SelectedFile is not null && point.Locations.Any(l => l.Path == SelectedFile.Path))
        {
            RebuildCurrentDiff();
        }
    }

    private void RefreshPointDerivedState()
    {
        OnPropertyChanged(nameof(OpenPoints));
        OnPropertyChanged(nameof(PositionLabel));
        OnPropertyChanged(nameof(ReviewFocusLabel));
        UpdateMarkers();
    }

    // Files and diff -------------------------------------------------------------------------------

    [RelayCommand]
    public async Task SelectFileAsync(FileEntryViewModel? file)
    {
        if (file is null)
        {
            return;
        }

        SelectedFile = file;
        SelectedNode = _nodes.GetValueOrDefault(file.Path);
        var version = ++_diffVersion;

        if (!_diffCache.TryGetValue(file.Path, out var diff))
        {
            IsDiffLoading = true;
            try
            {
                diff = await _source.GetFileDiffAsync(Ref, file.Path, _lifetime.Token).ConfigureAwait(true);
                _diffCache[file.Path] = diff;
            }
            catch (RpcException ex)
            {
                Toast = $"Couldn't load {file.Name}: {ex.Status.Detail}";
                return;
            }
            finally
            {
                if (version == _diffVersion)
                {
                    IsDiffLoading = false;
                }
            }
        }

        if (ShowFullFile)
        {
            await LoadHeadAsync(file, version).ConfigureAwait(true);
        }

        if (version == _diffVersion)
        {
            RebuildCurrentDiff();
        }
    }

    partial void OnShowFullFileChanged(bool value) => _ = ApplyShowFullFileAsync();

    private async Task ApplyShowFullFileAsync()
    {
        if (SelectedFile is not { } file)
        {
            return;
        }

        var version = _diffVersion;
        if (ShowFullFile)
        {
            await LoadHeadAsync(file, version).ConfigureAwait(true);
        }

        if (version == _diffVersion)
        {
            RebuildCurrentDiff();
        }
    }

    private async Task LoadHeadAsync(FileEntryViewModel file, int version)
    {
        if (_headCache.ContainsKey(file.Path))
        {
            return;
        }

        IsDiffLoading = true;
        try
        {
            var source = await _source.GetSourceFileAsync(Ref, file.Path, _lifetime.Token).ConfigureAwait(true);
            _headCache[file.Path] = source.Exists ? source.Text : null;
        }
        catch (RpcException ex)
        {
            // Not cached, so the next toggle or visit retries; meanwhile the plain diff stays up.
            Toast = $"Couldn't load the full {file.Name}: {ex.Status.Detail}";
        }
        finally
        {
            if (version == _diffVersion)
            {
                IsDiffLoading = false;
            }
        }
    }

    /// <summary>Flips the file's "Viewed" tick at once and saves it to GitHub; puts it back if GitHub refuses.</summary>
    [RelayCommand]
    public async Task ToggleViewedAsync(FileEntryViewModel? file)
    {
        if (file is null)
        {
            return;
        }

        var viewed = !file.IsViewed;
        file.IsViewed = viewed;
        OnPropertyChanged(nameof(ViewedSummary));
        try
        {
            await _source.SetFileViewedAsync(Ref, file.Path, viewed, _lifetime.Token).ConfigureAwait(true);
        }
        catch (RpcException ex) when (!(ex.StatusCode == StatusCode.Cancelled && _lifetime.IsCancellationRequested))
        {
            file.IsViewed = !viewed;
            OnPropertyChanged(nameof(ViewedSummary));
            Toast = $"Couldn't mark {file.Name} as {(viewed ? "viewed" : "not viewed")}: {ex.Status.Detail}";
        }
    }

    private void RebuildCurrentDiff()
    {
        if (SelectedFile is null || !_diffCache.TryGetValue(SelectedFile.Path, out var diff))
        {
            return;
        }

        var path = SelectedFile.Path;
        var cards = ReviewPoints
            .SelectMany(p => p.Locations.Where(l => l.Path == path).Select(l => (Id: CardId(p, l), NewLine: l.Line)));
        var head = ShowFullFile ? _headCache.GetValueOrDefault(path) : null;
        CurrentDiff = DiffDocument.Build(diff, cards, head);
        UpdateMarkers();
    }

    /// <summary>The primary location gets the full card; other locations get a compact reference card.</summary>
    public static string CardId(ReviewPointViewModel point, LocationViewModel location) =>
        location.IsPrimary ? point.Id : string.Create(CultureInfo.InvariantCulture, $"{point.Id}@{location.Path}:{location.Line}");

    public (ReviewPointViewModel Point, bool IsPrimary)? ResolveCard(string cardId)
    {
        var at = cardId.IndexOf('@', StringComparison.Ordinal);
        var id = at < 0 ? cardId : cardId[..at];
        return ReviewPoints.FirstOrDefault(p => p.Id == id) is { } point ? (point, at < 0) : null;
    }

    private void UpdateMarkers()
    {
        if (SelectedFile is null)
        {
            Markers = [];
            FocusRange = null;
            return;
        }

        var path = SelectedFile.Path;
        Markers =
        [
            .. ReviewPoints
                .Where(p => !p.IsDismissed)
                .SelectMany(p => p.Locations.Where(l => l.Path == path).Select(l => new GutterMarker(
                    p.Id, l.Line, p.Kind, p == CurrentPoint, p.IsCommented, _arrived.Remove(p.Id)))),
        ];

        FocusRange = CurrentPoint?.Locations.FirstOrDefault(l => l.Path == path) is { } focus
            ? (focus.Line, focus.Line)
            : null;
    }

    // Guided navigation ----------------------------------------------------------------------------

    [RelayCommand]
    public Task NextPointAsync() => StepAsync(+1);

    [RelayCommand]
    public Task PreviousPointAsync() => StepAsync(-1);

    private Task StepAsync(int direction)
    {
        var open = OpenPoints.ToList();
        if (open.Count == 0)
        {
            return Task.CompletedTask;
        }

        var index = CurrentPoint is null ? -1 : open.IndexOf(CurrentPoint);
        var next = index < 0
            ? (direction > 0 ? open[0] : open[^1])
            : open[(index + direction + open.Count) % open.Count];
        return GoToAsync(next, direction > 0 ? NavigationReason.Next : NavigationReason.Previous);
    }

    [RelayCommand]
    public Task SelectPointAsync(ReviewPointViewModel? point) =>
        point is null ? Task.CompletedTask : GoToAsync(point, NavigationReason.Select);

    public async Task GoToAsync(ReviewPointViewModel point, NavigationReason reason, LocationViewModel? location = null)
    {
        var previous = CurrentPoint;
        location ??= point.Locations[0];

        if (SelectedFile?.Path != location.Path && Files.FirstOrDefault(f => f.Path == location.Path) is { } file)
        {
            await SelectFileAsync(file).ConfigureAwait(true);
        }

        if (previous is not null)
        {
            previous.IsCurrent = false;
        }

        point.IsCurrent = true;
        CurrentPoint = point;
        UpdateMarkers();
        OnPropertyChanged(nameof(PositionLabel));
        NavigationRequested?.Invoke(this, new NavigationRequest(point, reason, previous));
    }

    [RelayCommand]
    public async Task GoToLocationAsync(LocationViewModel? location)
    {
        if (location is null || CurrentPoint is null)
        {
            return;
        }

        await GoToAsync(CurrentPoint, NavigationReason.Refocus, location).ConfigureAwait(true);
    }

    // Examine --------------------------------------------------------------------------------------

    [RelayCommand]
    public async Task ExamineAsync(ReviewPointViewModel? point)
    {
        point ??= CurrentPoint;
        if (point is null)
        {
            return;
        }

        if (point != CurrentPoint)
        {
            await GoToAsync(point, NavigationReason.Select).ConfigureAwait(true);
        }

        Mode = ReviewMode.Examine;
        if (point.State == ReviewPointState.Visible)
        {
            _ = SetStateQuietlyAsync(point, ReviewPointState.Examined);
        }
    }

    [RelayCommand]
    public async Task ShowPrecedentAsync(ReviewPointViewModel? point)
    {
        ExamineTab = "precedent";
        await ExamineAsync(point).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task ShowEvidenceAsync(ReviewPointViewModel? point)
    {
        ExamineTab = "evidence";
        await ExamineAsync(point).ConfigureAwait(true);
    }

    [RelayCommand]
    public void CloseExamine()
    {
        Mode = ReviewMode.Diff;
        if (CurrentPoint is { } point)
        {
            NavigationRequested?.Invoke(this, new NavigationRequest(point, NavigationReason.Refocus, point));
        }
    }

    // Decisions ------------------------------------------------------------------------------------

    [RelayCommand]
    public async Task DismissAsync(ReviewPointViewModel? point)
    {
        point ??= CurrentPoint;
        if (point is null || point.IsDismissed)
        {
            return;
        }

        var open = OpenPoints.ToList();
        var index = open.IndexOf(point);
        point.IsComposing = false;
        point.State = ReviewPointState.Dismissed;
        RefreshPointDerivedState();
        await SetStateQuietlyAsync(point, ReviewPointState.Dismissed).ConfigureAwait(true);

        // Move on to the next open point: dismissing is a decision, and the reviewer should keep flowing.
        var remaining = OpenPoints.ToList();
        if (remaining.Count > 0 && point == CurrentPoint)
        {
            await GoToAsync(remaining[Math.Min(Math.Max(index, 0), remaining.Count - 1)], NavigationReason.Next).ConfigureAwait(true);
        }
        else if (remaining.Count == 0)
        {
            Mode = ReviewMode.Diff;
        }
    }

    [RelayCommand]
    public async Task RestoreAsync(ReviewPointViewModel? point)
    {
        if (point is null)
        {
            return;
        }

        point.State = ReviewPointState.Visible;
        RefreshPointDerivedState();
        await SetStateQuietlyAsync(point, ReviewPointState.Visible).ConfigureAwait(true);
    }

    [RelayCommand]
    public void StartComment(ReviewPointViewModel? point)
    {
        point ??= CurrentPoint;
        if (point is null || point.IsDismissed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(point.CommentDraft))
        {
            point.CommentDraft = point.SuggestedComment;
        }

        point.PostError = null;
        point.IsComposing = true;
        if (Mode == ReviewMode.Examine)
        {
            ExamineTab = "comment";
        }
    }

    [RelayCommand]
    public void CancelComment(ReviewPointViewModel? point)
    {
        point ??= CurrentPoint;
        if (point is not null)
        {
            point.IsComposing = false;
            point.PostError = null;
        }
    }

    /// <summary>Posts to GitHub. Only ever triggered by an explicit reviewer action.</summary>
    [RelayCommand]
    public async Task PostCommentAsync(ReviewPointViewModel? point)
    {
        point ??= CurrentPoint;
        if (point is null || point.IsPosting || string.IsNullOrWhiteSpace(point.CommentDraft))
        {
            return;
        }

        point.IsPosting = true;
        point.PostError = null;
        try
        {
            var reply = await _source.PostCommentAsync(
                new PostReviewCommentRequest
                {
                    PullRequest = Ref,
                    ReviewPointId = point.Id,
                    Path = point.Path,
                    Line = point.Line,
                    Body = point.CommentDraft.Trim(),
                },
                _lifetime.Token).ConfigureAwait(true);

            point.PostedUrl = reply.Url;
            point.State = ReviewPointState.Commented;
            point.IsComposing = false;
            RefreshPointDerivedState();
            Toast = "Comment posted to GitHub";
        }
        catch (RpcException ex)
        {
            point.PostError = ex.Status.Detail;
        }
        finally
        {
            point.IsPosting = false;
        }
    }

    private async Task SetStateQuietlyAsync(ReviewPointViewModel point, ReviewPointState state)
    {
        try
        {
            await _source.SetReviewPointStateAsync(Ref, point.Id, state, _lifetime.Token).ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            Toast = $"Couldn't save: {ex.Status.Detail}";
        }
    }

    [RelayCommand]
    public void ToggleGenerated() => ShowGenerated = !ShowGenerated;

    // Precedent source ------------------------------------------------------------------------------

    [ObservableProperty]
    public partial SourcePreviewViewModel? SourcePreview { get; set; }

    [RelayCommand]
    public async Task OpenSourceAsync(PrecedentExampleViewModel? example)
    {
        if (example is null)
        {
            return;
        }

        try
        {
            var file = await _source.GetSourceFileAsync(Ref, example.Path, _lifetime.Token).ConfigureAwait(true);
            SourcePreview = file.Exists
                ? new SourcePreviewViewModel(example.Path, example.TypeName, file.Text, example.Line)
                : new SourcePreviewViewModel(example.Path, example.TypeName, example.Snippet, example.Line, example.StartLine);
        }
        catch (RpcException ex)
        {
            Toast = $"Couldn't open {example.FileName}: {ex.Status.Detail}";
        }
    }

    [RelayCommand]
    public void CloseSource() => SourcePreview = null;

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _watchCts.Dispose();
    }
}
