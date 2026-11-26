using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Lumen.App.Diff;
using Lumen.App.Services;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

/// <summary>
/// One open review: a GitHub pull request or a local branch. Its files and diff, folds, threads and the pending review;
/// slices, flags and marks live in their own partials.
/// </summary>
public sealed partial class PullRequestViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IReviewSource _source;
    private readonly CancellationTokenSource _lifetime = new();

    // Keyed by scope, whitespace mode and path (see DiffKey).
    private readonly Dictionary<string, FileDiff> _diffCache = new(StringComparer.Ordinal);

    // Head-side file text for the full-file view; null when the file has none (deleted) or couldn't be loaded.
    private readonly Dictionary<string, string?> _headCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileTreeNode> _nodes = new(StringComparer.Ordinal);
    private readonly WatchPullRequestRequest _watchRequest;
    private Task? _watch;
    private int _diffVersion;

    public PullRequestViewModel(IReviewSource source, PullRequestRef pullRequest)
        : this(source, new WatchPullRequestRequest { PullRequest = pullRequest })
    {
    }

    /// <summary>Opens what <paramref name="request"/> names: a pull request, or a local branch (<c>request.Local</c>).</summary>
    public PullRequestViewModel(IReviewSource source, WatchPullRequestRequest request)
    {
        _source = source;
        _watchRequest = request.Clone();
        _watchRequest.Refresh = false;
        Ref = request.PullRequest ?? new PullRequestRef { Owner = "local", Name = LocalName(request.Local) };
        Local = request.Local;
        Title = Local is null ? $"{Ref.Owner}/{Ref.Name} #{Ref.Number}" : LocalName(Local);
        Status = "Connecting…";
    }

    private static string LocalName(LocalBranchRef? local) =>
        local is null ? "" : $"{(local.WorkingTree ? "working tree" : local.HeadRef)} vs {local.BaseRef}";

    /// <summary>The pull request, or for a local branch the synthetic ref the engine gives the session (owner "local").</summary>
    public PullRequestRef Ref { get; private set; }

    /// <summary>Set when reviewing a local branch: posting controls are hidden and nothing goes to GitHub.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocal), nameof(CanPost), nameof(NumberLabel), nameof(RepositoryLabel), nameof(LocalLabel))]
    public partial LocalBranchRef? Local { get; private set; }

    public bool IsLocal => Local is not null;

    /// <summary>Comments, reviews and Viewed ticks only exist for pull requests.</summary>
    public bool CanPost => !IsLocal;

    public string NumberLabel => IsLocal ? "Local" : $"#{Ref.Number}";

    public string RepositoryLabel => IsLocal ? Local!.ClonePath : $"{Ref.Owner} / {Ref.Name}";

    /// <summary>"C:\src\app · against main · Local branch: comments stay on this machine".</summary>
    public string LocalLabel => Local is null ? "" : $"{Local.ClonePath} · against {Local.BaseRef} · Local branch: nothing is posted";

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

    /// <summary>The head commit (or, for a working tree, the snapshot tree) the review shows.</summary>
    public string HeadSha => _headSha;

    public string HeadShort => _headSha.Length > 7 ? _headSha[..7] : _headSha;

    // Folds ----------------------------------------------------------------------------------------

    /// <summary>What folds, per hunk; null until the engine sends it (or when it could not compute it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTriage), nameof(ChangedLines), nameof(OpenLines), nameof(BoilerplateLines), nameof(MechanicalLines),
        nameof(ShrinkLabel))]
    public partial TriageReady? Triage { get; set; }

    public bool HasTriage => Triage is not null;

    public int ChangedLines => Triage?.Summary?.TotalLines ?? Additions + Deletions;

    /// <summary>Lines left open by default: what there is to read.</summary>
    public int OpenLines => Triage?.Summary?.OpenLines ?? 0;

    public int BoilerplateLines => Triage?.Summary?.BoilerplateLines ?? 0;

    public int MechanicalLines => Triage?.Summary?.MechanicalLines ?? 0;

    /// <summary>"1,180 lines to read of 4,212 changed".</summary>
    public string ShrinkLabel => Triage is null
        ? string.Create(CultureInfo.InvariantCulture, $"{ChangedLines:N0} changed")
        : string.Create(CultureInfo.InvariantCulture, $"{OpenLines:N0} lines to read of {ChangedLines:N0} changed");

    /// <summary>Folds the reviewer opened; everything else proven or summarised stays behind its one-line reason.</summary>
    private readonly HashSet<string> _expandedFolds = new(StringComparer.Ordinal);

    /// <summary>Folds opened at least once this session, for "Mark reviewed" ("2 folds not opened").</summary>
    private readonly HashSet<string> _openedFolds = new(StringComparer.Ordinal);

    public bool IsFoldOpened(string foldId) => _openedFolds.Contains(foldId);

    public void ToggleFold(string foldId)
    {
        if (!_expandedFolds.Remove(foldId))
        {
            _expandedFolds.Add(foldId);
            _openedFolds.Add(foldId);
        }

        RebuildCurrentDiff();
        OnFoldsChanged();
    }

    /// <summary>Opens every fold in the files given (the "open all folds in slice" key).</summary>
    public void OpenFolds(IEnumerable<string> paths)
    {
        var set = paths.ToHashSet(StringComparer.Ordinal);
        foreach (var hunk in Triage?.Hunks.Where(h => set.Contains(h.Path) && DiffFolds.Folds(h)) ?? [])
        {
            var id = DiffFolds.IdFor(hunk);
            _expandedFolds.Add(id);
            _openedFolds.Add(id);
        }

        RebuildCurrentDiff();
        OnFoldsChanged();
    }

    partial void OnFoldsChanged();

    public FoldBadgeViewModel? ResolveFold(string cardId)
    {
        var id = cardId.StartsWith(Diff.DiffFold.CardPrefix, StringComparison.Ordinal) ? cardId[Diff.DiffFold.CardPrefix.Length..] : cardId;
        if (Triage?.Hunks.FirstOrDefault(h => DiffFolds.IdFor(h) == id) is not { } hunk)
        {
            return null;
        }

        var group = hunk.GroupId.Length == 0 ? null : Triage.Groups.FirstOrDefault(g => g.Id == hunk.GroupId);
        var members = group is null ? [] : Triage.Hunks.Where(h => h.GroupId == group.Id).ToList();
        var index = members.FindIndex(m => DiffFolds.IdFor(m) == id);
        return new FoldBadgeViewModel(this, id, hunk, _expandedFolds.Contains(id), group, members, Math.Max(0, index));
    }

    /// <summary>Opens another hunk of the same group, so a rename or move can be followed across files.</summary>
    public async Task GoToGroupMemberAsync(IReadOnlyList<HunkTriage> members, int index)
    {
        if (index < 0 || index >= members.Count)
        {
            return;
        }

        var hunk = members[index];
        if (Files.FirstOrDefault(f => f.Path == hunk.Path) is { } file && SelectedFile?.Path != hunk.Path)
        {
            await SelectFileAsync(file).ConfigureAwait(true);
        }

        var start = hunk.NewStart > 0 ? hunk.NewStart : hunk.OldStart;
        var end = hunk.NewEnd > 0 ? hunk.NewEnd : hunk.OldEnd;
        FocusRange = (start, end);
        LineFocusRequested?.Invoke(this, (start, hunk.NewStart == 0));
    }

    /// <summary>Asks the view to scroll to a line (head side unless the flag says old).</summary>
    public event EventHandler<(int Line, bool OldSide)>? LineFocusRequested;

    partial void OnTriageChanged(TriageReady? value)
    {
        RebuildCurrentDiff();
        foreach (var file in Files)
        {
            file.FoldLines = value?.Files.FirstOrDefault(f => f.Path == file.Path)?.FoldLines;
        }

        OnPropertyChanged(nameof(GeneratedSummary));
        OnTriageApplied();
    }

    partial void OnTriageApplied();

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

    /// <summary>Nav rail count: viewed / total files.</summary>
    public string ViewedCount => string.Create(CultureInfo.InvariantCulture, $"{Files.Count(f => f.IsViewed)} / {Files.Count}");

    public string ViewedSummary => string.Create(CultureInfo.InvariantCulture, $"{Files.Count(f => f.IsViewed)} / {Files.Count} viewed");

    public string GeneratedSummary => GeneratedFiles.Count == 0
        ? ""
        : GeneratedFiles.All(f => f.FoldLines is not null)
            ? string.Create(CultureInfo.InvariantCulture, $"{GeneratedFiles.Count} generated files · {GeneratedFiles.Sum(f => f.FoldLines!.Mechanical):N0} lines")
            : string.Create(CultureInfo.InvariantCulture, $"{GeneratedFiles.Count} generated files · +{GeneratedFiles.Sum(f => f.Model.Additions)}");

    [ObservableProperty]
    public partial string? Toast { get; set; }

    // Loading --------------------------------------------------------------------------------------

    public void Start() => _watch ??= WatchAsync(refresh: false);

    /// <summary>A review setting changed since this pull request was analysed; offer Re-analyse, never run it unasked.</summary>
    [ObservableProperty]
    public partial bool NeedsReanalysis { get; set; }

    public string ReanalyseLabel => IsLocal ? "Re-analyse" : $"Re-analyse {NumberLabel}";

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
        _watchCts.Dispose();
        _watchCts = new CancellationTokenSource();
        ++_diffVersion;
        _diffCache.Clear();
        _headCache.Clear();
        _expandedFolds.Clear();
        Triage = null;
        Files.Clear();
        GeneratedFiles.Clear();
        Tree.Clear();
        _nodes.Clear();
        SelectedFile = null;
        SelectedNode = null;
        CurrentDiff = null;
        Markers = [];
        FocusRange = null;
        IsDiffLoading = false;
        IsAnalysing = true;
        IsLoading = true;
        IsAuthenticationError = false;
        _completeStatus = "";
        OnPropertyChanged(nameof(ViewedSummary));
        OnPropertyChanged(nameof(ViewedCount));
        OnPropertyChanged(nameof(GeneratedSummary));
        Error = null;
        NewHeadLabel = null;
        Scope = null;
        ScopeFiles.Clear();
        Timeline.Clear();
        Checks.Clear();
        ResetReviewClientState();
        ResetOverhaulState();
    }

    partial void ResetOverhaulState();

    private async Task WatchAsync(bool refresh)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _watchCts.Token);
        var token = linked.Token;
        var request = _watchRequest.Clone();
        request.Refresh = refresh;
        try
        {
            await foreach (var evt in _source.WatchAsync(request, token).ConfigureAwait(true))
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

            case PullRequestEvent.EventOneofCase.SlicesReady:
                ApplySlices(evt.SlicesReady);
                break;

            case PullRequestEvent.EventOneofCase.FlagsReady:
                ApplyFlags(evt.FlagsReady);
                break;

            case PullRequestEvent.EventOneofCase.MarksUpdated:
                ApplyMarks(evt.MarksUpdated);
                break;

            case PullRequestEvent.EventOneofCase.FileViewed:
                if (Files.FirstOrDefault(f => f.Path == evt.FileViewed.Path) is { } viewedFile)
                {
                    viewedFile.IsViewed = evt.FileViewed.Viewed;
                    OnPropertyChanged(nameof(ViewedSummary));
                    OnPropertyChanged(nameof(ViewedCount));
                }

                break;

            case PullRequestEvent.EventOneofCase.Complete:
                IsAnalysing = false;
                _completeStatus = string.Create(CultureInfo.InvariantCulture, $"Analysed in {evt.Complete.DurationMs / 1000.0:0.0}s");
                Status = _completeStatus;
                break;

            case PullRequestEvent.EventOneofCase.Failed:
                Fail(evt.Failed.Message, evt.Failed.IsAuthentication);
                break;

            case PullRequestEvent.EventOneofCase.TimelineUpdated:
                ApplyTimeline(evt.TimelineUpdated.Timeline);
                break;

            case PullRequestEvent.EventOneofCase.ChecksUpdated:
                ApplyChecks(evt.ChecksUpdated.Checks);
                break;

            case PullRequestEvent.EventOneofCase.HeadChanged:
                ApplyHeadChanged(evt.HeadChanged);
                break;

            case PullRequestEvent.EventOneofCase.ThreadsUpdated:
                ApplyThreads(evt.ThreadsUpdated.Threads);
                break;

            case PullRequestEvent.EventOneofCase.PendingReviewUpdated:
                ApplyPendingReview(evt.PendingReviewUpdated);
                break;
        }
    }

    partial void ApplySlices(SlicesReady slices);

    partial void ApplyFlags(FlagsReady flags);

    partial void ApplyMarks(MarksUpdated marks);

    private async Task ApplySnapshotAsync(Contracts.PullRequestSnapshot snapshot)
    {
        if (snapshot.PullRequest is { } actual)
        {
            // A local session's synthetic ref comes from the engine; every later request uses it.
            Ref = actual;
        }

        if (snapshot.Local is { } local)
        {
            Local = local;
        }

        Title = snapshot.Title;
        Author = snapshot.Author;
        ViewerLogin = snapshot.ViewerLogin;
        StateLabel = IsLocal ? "Read-only" : snapshot.IsDraft ? "Draft" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(snapshot.State);
        Url = snapshot.Url;
        BranchLabel = $"{snapshot.HeadRef} → {snapshot.BaseRef}";
        Additions = snapshot.Files.Sum(f => f.Additions);
        Deletions = snapshot.Files.Sum(f => f.Deletions);
        _headSha = snapshot.HeadSha;
        OnPropertyChanged(nameof(HeadSha));
        OnPropertyChanged(nameof(HeadShort));
        OnPropertyChanged(nameof(ChangedLines));
        OnPropertyChanged(nameof(ShrinkLabel));
        _snapshotBody = snapshot.Body;
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasDescription));
        _mergeBaseSha = snapshot.MergeBaseSha.Length > 0 ? snapshot.MergeBaseSha : snapshot.BaseSha;
        OnPropertyChanged(nameof(ViewerIsAuthor));
        OnPropertyChanged(nameof(CanApprove));
        OnPropertyChanged(nameof(ApproveHint));
        if (!IsLocal)
        {
            _ = LoadDetailsAsync();
        }

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
        OnPropertyChanged(nameof(ViewedCount));
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
        var source = Scope is null ? (IEnumerable<FileEntryViewModel>)Files : ScopeFiles;
        var visible = source.Where(f => !f.IsMechanical && (filter.Length == 0 || f.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)));
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
        IsDiffLoading = false;
        if (CurrentDiff?.Path != file.Path)
        {
            CurrentDiff = null;
            Markers = [];
            FocusRange = null;
        }

        var reload = _watchCts;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, reload.Token);

        if (!_diffCache.TryGetValue(DiffKey(file.Path), out var diff))
        {
            IsDiffLoading = true;
            try
            {
                var key = DiffKey(file.Path);
                var request = new GetFileDiffRequest { PullRequest = Ref, Path = file.Path, IgnoreWhitespace = IgnoreWhitespace };
                if (Scope is { } scope)
                {
                    request.Range = new CommitRange { FromSha = scope.FromSha, ToSha = scope.ToSha };
                }

                diff = await _source.GetFileDiffAsync(request, linked.Token).ConfigureAwait(true);
                if (linked.IsCancellationRequested)
                {
                    return;
                }

                _diffCache[key] = diff;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException ex)
            {
                if (!linked.IsCancellationRequested && version == _diffVersion)
                {
                    Toast = $"Couldn't load {file.Name}: {ex.Status.Detail}";
                }

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

        OnFileSelected(file);
    }

    partial void OnFileSelected(FileEntryViewModel file);

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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _watchCts.Token);
        try
        {
            var source = await _source.GetSourceFileAsync(Ref, file.Path, linked.Token).ConfigureAwait(true);
            if (linked.IsCancellationRequested)
            {
                return;
            }

            _headCache[file.Path] = source.Exists ? source.Text : null;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (RpcException ex)
        {
            // Not cached, so the next toggle or visit retries; meanwhile the plain diff stays up.
            if (!linked.IsCancellationRequested && version == _diffVersion)
            {
                Toast = $"Couldn't load the full {file.Name}: {ex.Status.Detail}";
            }
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
        if (file is null || !CanPost)
        {
            return;
        }

        var viewed = !file.IsViewed;
        var twins = Files.Concat(ScopeFiles).Where(f => f.Path == file.Path).Append(file).Distinct().ToList();
        twins.ForEach(f => f.IsViewed = viewed);
        OnPropertyChanged(nameof(ViewedSummary));
        OnPropertyChanged(nameof(ViewedCount));
        try
        {
            await _source.SetFileViewedAsync(Ref, file.Path, viewed, _lifetime.Token).ConfigureAwait(true);
        }
        catch (RpcException ex) when (!(ex.StatusCode == StatusCode.Cancelled && _lifetime.IsCancellationRequested))
        {
            twins.ForEach(f => f.IsViewed = !viewed);
            OnPropertyChanged(nameof(ViewedSummary));
            OnPropertyChanged(nameof(ViewedCount));
            Toast = $"Couldn't mark {file.Name} as {(viewed ? "viewed" : "not viewed")}: {ex.Status.Detail}";
        }
    }

    private string DiffKey(string path) => $"{Scope?.Key ?? "pr"}|{(IgnoreWhitespace ? "w" : "")}|{path}";

    private void RebuildCurrentDiff()
    {
        if (SelectedFile is null || !_diffCache.TryGetValue(DiffKey(SelectedFile.Path), out var diff))
        {
            return;
        }

        var path = SelectedFile.Path;
        var anchors = ScopeEndsAtHead ? CardAnchors(path) : CommitCardAnchors(path);

        // The full file is the head's; a range that ends earlier shows its hunks only.
        var head = ShowFullFile && ScopeEndsAtHead ? _headCache.GetValueOrDefault(path) : null;

        // Triage spans are lines of the whole pull request's diff, so folds apply only when no range is chosen.
        var folds = Scope is null ? DiffFolds.For(Triage, path, _expandedFolds) : null;
        CurrentDiff = DiffDocument.Build(diff, anchors, head, folds);
        UpdateMarkers();
    }

    /// <summary>Everything that sits under a line of this file: red flags, threads and drafts.</summary>
    private IEnumerable<CardAnchor> CardAnchors(string path) =>
        FlagAnchors(path)
            .Concat(ThreadAnchors(path))
            .Concat(DraftAnchors(path))
            .Concat(OutdatedAnchors(path));

    private void UpdateMarkers()
    {
        if (SelectedFile is null)
        {
            Markers = [];
            return;
        }

        Markers = !ScopeEndsAtHead ? [] : [.. FlagMarkers(SelectedFile.Path)];
    }

    // Navigation -----------------------------------------------------------------------------------

    /// <summary>The files J/K and N/P step through: the current slice's in reading order, else the tree's order.</summary>
    private List<FileEntryViewModel> NavigableFiles()
    {
        var ordered = SliceFileOrder();
        if (ordered.Count > 0)
        {
            return ordered;
        }

        return [.. Tree.SelectMany(n => n.Descendants().Prepend(n)).Where(n => n.File is not null).Select(n => n.File!)];
    }

    /// <summary>Next file in reading order; on to the next slice after its last file.</summary>
    [RelayCommand]
    public Task NextFileAsync() => StepFileOrSliceAsync(+1);

    [RelayCommand]
    public Task PreviousFileAsync() => StepFileOrSliceAsync(-1);

    private async Task StepFileOrSliceAsync(int direction)
    {
        if (!await StepFileAsync(direction).ConfigureAwait(true))
        {
            await StepPastSliceAsync(direction).ConfigureAwait(true);
        }
    }

    public string FilePositionLabel
    {
        get
        {
            var files = NavigableFiles();
            var index = SelectedFile is null ? -1 : files.FindIndex(f => f.Path == SelectedFile.Path);
            return files.Count == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{(index < 0 ? 0 : index + 1)} of {files.Count}");
        }
    }

    partial void OnSelectedFileChanged(FileEntryViewModel? value) => OnPropertyChanged(nameof(FilePositionLabel));

    [RelayCommand]
    public void ToggleGenerated() => ShowGenerated = !ShowGenerated;

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _watchCts.Dispose();
    }
}
