using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Lumen.App.Diff;
using Lumen.App.Services;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

/// <summary>
/// Feature slices, red flags, reviewed marks and since-my-last-look (docs/overhaul.md). Slices replace the review
/// plan: the left pane lists them, the centre shows one slice at a time with its files in reading order, and the right
/// pane says why the files belong together and which flags they carry.
/// </summary>
public sealed partial class PullRequestViewModel
{
    private const string FlagCardPrefix = "flag:";
    private readonly List<SliceViewModel> _slices = [];
    private readonly Dictionary<string, UnitFingerprint> _units = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _marks = new(StringComparer.Ordinal);
    private SinceLastLook? _sinceLook;

    // Slices ---------------------------------------------------------------------------------------

    public ObservableCollection<SliceGroupViewModel> SliceGroups { get; } = [];

    public IReadOnlyList<SliceViewModel> Slices => _slices;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsFileTree), nameof(SliceCountLabel))]
    public partial bool HasSlices { get; private set; }

    /// <summary>Slicing ran but fell back to the file list (not Razor/WebForms-shaped, failed or over budget).</summary>
    [ObservableProperty]
    public partial string? SliceFallbackReason { get; private set; }

    /// <summary>The engine's progress stage while it builds slices (OverhaulAnalysis.SlicesStage).</summary>
    internal const string SlicesStage = "slices";

    private readonly Stopwatch _slicingClock = new();
    private DispatcherTimer? _slicingTimer;

    /// <summary>
    /// Slicing is under way. It runs after the diff, folds and flags arrive and can take minutes on a big change, so
    /// the file list says so and counts up; the slices replace it when they're ready.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSlicing { get; private set; }

    /// <summary>The engine's current step: "Building feature slices: linking files…".</summary>
    [ObservableProperty]
    public partial string SlicingStatus { get; private set; } = "";

    /// <summary>How long slicing has run, as m:ss.</summary>
    [ObservableProperty]
    public partial string SlicingElapsedLabel { get; private set; } = "";

    private void OnSlicingProgress(string message)
    {
        SlicingStatus = message;
        if (IsSlicing)
        {
            return;
        }

        IsSlicing = true;
        _slicingClock.Restart();
        TickSlicing();
        _slicingTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickSlicing());
        _slicingTimer.Start();
    }

    internal void TickSlicing() => SlicingElapsedLabel = FormatElapsed(_slicingClock.Elapsed);

    internal static string FormatElapsed(TimeSpan elapsed) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}");

    private void StopSlicing()
    {
        _slicingTimer?.Stop();
        _slicingClock.Reset();
        IsSlicing = false;
        SlicingStatus = "";
        SlicingElapsedLabel = "";
    }

    /// <summary>The file tree shows when chosen, or when there are no slices to show instead.</summary>
    public bool ShowsFileTree => ShowFileList || !HasSlices;

    public string SliceCountLabel => HasSlices ? _slices.Count(s => !s.IsMechanicalOnly).ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>"Pull request" or "Local branch", over the details in the file list.</summary>
    public string ReviewKindLabel => IsLocal ? "Local branch" : "Pull request";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentSlice), nameof(CurrentSliceFlags), nameof(CurrentSliceFlagCount), nameof(IsMechanicalSlice), nameof(MechanicalGroups))]
    public partial SliceViewModel? CurrentSlice { get; private set; }

    public bool HasCurrentSlice => CurrentSlice is not null;

    public bool IsMechanicalSlice => CurrentSlice?.IsMechanicalOnly == true;

    /// <summary>
    /// "Mechanical only" as groups, not files (docs/overhaul.md, UI): "Renamed `GetOrder`→`GetOrderAsync` · 12 sites",
    /// "Database migration · 3 files". Largest first.
    /// </summary>
    public IReadOnlyList<string> MechanicalGroups
    {
        get
        {
            if (CurrentSlice is not { IsMechanicalOnly: true } slice || Triage is not { } triage)
            {
                return [];
            }

            var paths = slice.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            var hunks = triage.Hunks.Where(h => paths.Contains(h.Path) && h.Fold == FoldKind.Mechanical).ToList();
            var groups = triage.Groups.ToDictionary(g => g.Id, StringComparer.Ordinal);
            var grouped = hunks.Where(h => h.GroupId.Length > 0 && groups.ContainsKey(h.GroupId)).GroupBy(h => h.GroupId)
                .Select(g => (Lines: g.Sum(h => h.ChangedLines), Text: string.Create(CultureInfo.InvariantCulture,
                    $"{groups[g.Key].Title} · {groups[g.Key].MemberCount} site{(groups[g.Key].MemberCount == 1 ? "" : "s")}")));
            var rest = hunks.Where(h => h.GroupId.Length == 0 || !groups.ContainsKey(h.GroupId)).GroupBy(h => h.Reasons.FirstOrDefault() ?? "Mechanical")
                .Select(g => (Lines: g.Sum(h => h.ChangedLines), Text: string.Create(CultureInfo.InvariantCulture,
                    $"{g.Key} · {g.Select(h => h.Path).Distinct().Count()} file{(g.Select(h => h.Path).Distinct().Count() == 1 ? "" : "s")}")));
            return [.. grouped.Concat(rest).OrderByDescending(g => g.Lines).Select(g => g.Text)];
        }
    }

    public MarkReviewedViewModel MarkReviewed { get; } = new();

    /// <summary>"3 of 8 slices reviewed · 760 lines left · 2 changed since you marked them".</summary>
    [ObservableProperty]
    public partial string SliceProgressLabel { get; private set; } = "";

    [ObservableProperty]
    public partial int ReviewedSliceCount { get; private set; }

    partial void ApplySlices(SlicesReady slices)
    {
        StopSlicing();
        _slices.Clear();
        _units.Clear();
        SliceGroups.Clear();
        CurrentSlice = null;
        if (slices.FellBack)
        {
            SliceFallbackReason = slices.FallbackReason.Length > 0 ? slices.FallbackReason : "Slices aren't available for this change.";
            HasSlices = false;
            return;
        }

        foreach (var unit in slices.Units)
        {
            _units[unit.Unit] = unit;
        }

        var edges = slices.Edges.Select(e => new SliceEdgeViewModel(e)).ToList();
        foreach (var slice in slices.Slices)
        {
            var files = slice.Files.OrderBy(f => f.Order).ThenBy(f => f.Path, StringComparer.Ordinal)
                .Select((f, i) => new SliceFileViewModel(f, Files.FirstOrDefault(e => e.Path == f.Path), i + 1))
                .ToList();
            var paths = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            var vm = new SliceViewModel(slice, files, [.. edges.Where(e => paths.Contains(PathOf(e.Model.From)) && paths.Contains(PathOf(e.Model.To)) && !e.IsPossible)]);
            foreach (var file in files)
            {
                vm.Units.AddRange(UnitsOf(file));
            }

            _slices.Add(vm);
        }

        HasSlices = _slices.Count > 0;
        SliceFallbackReason = null;
        RecountSlices();
        RebuildSliceGroups();
        RecomputeReviewed();
        if (HasSlices)
        {
            ShowFileList = false;
            _ = SelectSliceAsync(_slices.FirstOrDefault(s => s.IsFeature) ?? _slices[0]);
        }
    }

    private static string PathOf(string node) => node.Split("::", 2)[0];

    /// <summary>A file's units in a slice: the file itself, or the member units the slice holds in a large file.</summary>
    private List<string> UnitsOf(SliceFileViewModel file)
    {
        var all = _units.Values.Where(u => u.Path == file.Path).Select(u => u.Unit).ToList();
        if (all.Count == 0)
        {
            return [file.Path];
        }

        if (file.Model.Members.Count == 0 || all.Contains(file.Path))
        {
            return all;
        }

        var members = file.Model.Members.Select(m => $"{file.Path}::{m}").ToHashSet(StringComparer.Ordinal);
        var chosen = all.Where(members.Contains).ToList();
        return chosen.Count > 0 ? chosen : all;
    }

    private void RebuildSliceGroups()
    {
        SliceGroups.Clear();
        var visible = _slices.Where(s => _sinceLook is null || s.SinceLines > 0).ToList();
        var pages = visible.Where(s => s.IsFeature).ToList();
        var across = visible.Where(s => s.IsShared || s.IsUnlinked).ToList();
        var nothing = visible.Where(s => s.IsMechanicalOnly).ToList();
        if (pages.Count > 0)
        {
            SliceGroups.Add(new SliceGroupViewModel("Pages", pages));
        }

        if (across.Count > 0)
        {
            SliceGroups.Add(new SliceGroupViewModel("Across slices", across));
        }

        if (nothing.Count > 0)
        {
            SliceGroups.Add(new SliceGroupViewModel("Nothing to read", nothing));
        }
    }

    /// <summary>Lines and flags per slice and file, from the triage's fold lines and the flags.</summary>
    private void RecountSlices()
    {
        foreach (var slice in _slices)
        {
            int open = 0, changed = 0, boilerplate = 0, mechanical = 0;
            foreach (var file in slice.Files)
            {
                var lines = Triage?.Files.FirstOrDefault(f => f.Path == file.Path)?.FoldLines;
                var fileOpen = slice.Units.Where(u => u == file.Path || u.StartsWith(file.Path + "::", StringComparison.Ordinal))
                    .Sum(u => _units.TryGetValue(u, out var fp) ? fp.OpenLines : 0);
                if (fileOpen == 0 && lines is not null)
                {
                    fileOpen = lines.Open;
                }
                else if (fileOpen == 0 && file.File is { } entry)
                {
                    fileOpen = entry.Model.Additions + entry.Model.Deletions;
                }

                file.OpenLines = fileOpen;
                file.FlagCount = Flags.Count(f => f.Path == file.Path);
                open += fileOpen;
                changed += lines is null ? (file.File?.Model.Additions ?? 0) + (file.File?.Model.Deletions ?? 0) : lines.Open + lines.Boilerplate + lines.Mechanical;
                boilerplate += lines?.Boilerplate ?? 0;
                mechanical += lines?.Mechanical ?? 0;
            }

            slice.BoilerplateLines = boilerplate;
            slice.MechanicalLines = mechanical;
            slice.ChangedLines = changed;
            slice.OpenLines = slice.IsMechanicalOnly ? 0 : open;
            slice.FlagCount = slice.Files.Sum(f => f.FlagCount);
        }

        foreach (var file in Files)
        {
            file.FlagCount = Flags.Count(f => f.Path == file.Path);
        }

        OnPropertyChanged(nameof(CurrentSliceFlags));
        OnPropertyChanged(nameof(CurrentSliceFlagCount));
        UpdateMarkReviewed();
    }

    partial void OnTriageApplied() => RecountSlices();

    [RelayCommand]
    public async Task SelectSliceAsync(SliceViewModel? slice)
    {
        if (slice is null)
        {
            return;
        }

        foreach (var other in _slices)
        {
            other.IsCurrent = other == slice;
        }

        CurrentSlice = slice;
        MarkReviewed.IsArmed = false;
        UpdateMarkReviewed();
        ShowTab(PullRequestTab.Files);
        if (slice.Files.FirstOrDefault(f => f.File is not null)?.File is { } first && slice.Files.All(f => f.Path != SelectedFile?.Path))
        {
            await SelectFileAsync(first).ConfigureAwait(true);
        }

        HighlightSliceFile();
        OnPropertyChanged(nameof(FilePositionLabel));
    }

    [RelayCommand]
    public Task SelectSliceFileAsync(SliceFileViewModel? file) =>
        file?.File is { } entry ? SelectFileAsync(entry) : Task.CompletedTask;

    /// <summary>The slices J/K walk, in list order.</summary>
    private List<SliceViewModel> SliceOrder() => [.. SliceGroups.SelectMany(g => g.Slices)];

    [RelayCommand]
    public Task NextSliceAsync() => StepSliceAsync(+1, landOnLast: false);

    [RelayCommand]
    public Task PreviousSliceAsync() => StepSliceAsync(-1, landOnLast: false);

    private async Task StepSliceAsync(int direction, bool landOnLast)
    {
        var order = SliceOrder();
        if (order.Count == 0)
        {
            return;
        }

        var index = CurrentSlice is null ? -1 : order.IndexOf(CurrentSlice);
        var next = Math.Clamp(index + direction, 0, order.Count - 1);
        if (next == index)
        {
            return;
        }

        var slice = order[next];
        await SelectSliceAsync(slice).ConfigureAwait(true);
        var target = landOnLast ? slice.Files.LastOrDefault(f => f.File is not null) : slice.Files.FirstOrDefault(f => f.File is not null);
        if (target?.File is { } entry)
        {
            await SelectFileAsync(entry).ConfigureAwait(true);
        }
    }

    private List<FileEntryViewModel> SliceFileOrder() =>
        CurrentSlice is null || Scope is not null ? [] : [.. CurrentSlice.Files.Select(f => f.File).OfType<FileEntryViewModel>()];

    /// <summary>Past the last file of a slice, N goes on to the next slice (and P back to the previous one's last file).</summary>
    private Task StepPastSliceAsync(int direction) => HasSlices ? StepSliceAsync(direction, landOnLast: direction < 0) : Task.CompletedTask;

    partial void OnFileSelected(FileEntryViewModel file)
    {
        if (HasSlices && CurrentSlice?.Files.Any(f => f.Path == file.Path) != true
            && _slices.FirstOrDefault(s => s.Files.Any(f => f.Path == file.Path)) is { } owner)
        {
            foreach (var other in _slices)
            {
                other.IsCurrent = other == owner;
            }

            CurrentSlice = owner;
            UpdateMarkReviewed();
        }

        HighlightSliceFile();
    }

    private void HighlightSliceFile()
    {
        foreach (var slice in _slices)
        {
            foreach (var file in slice.Files)
            {
                file.IsCurrent = slice == CurrentSlice && file.Path == SelectedFile?.Path;
            }
        }

        OnPropertyChanged(nameof(CurrentSliceFile));
    }

    /// <summary>The selected file as a member of the current slice: its place in the reading order and why it is there.</summary>
    public SliceFileViewModel? CurrentSliceFile => CurrentSlice?.Files.FirstOrDefault(f => f.Path == SelectedFile?.Path);

    /// <summary>Open every fold in the current slice's files (Shift+O).</summary>
    [RelayCommand]
    public void OpenSliceFolds()
    {
        var paths = CurrentSlice is { } slice ? slice.Files.Select(f => f.Path) : SelectedFile is { } file ? [file.Path] : [];
        OpenFolds(paths);
    }

    /// <summary>Open the current file's folds, or close them if all are open (O).</summary>
    [RelayCommand]
    public void ToggleFileFolds()
    {
        if (SelectedFile is not { } file || Triage is null)
        {
            return;
        }

        var ids = Triage.Hunks.Where(h => h.Path == file.Path && DiffFolds.Folds(h)).Select(DiffFolds.IdFor).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        if (ids.All(_expandedFolds.Contains))
        {
            ids.ForEach(id => _expandedFolds.Remove(id));
        }
        else
        {
            foreach (var id in ids)
            {
                _expandedFolds.Add(id);
                _openedFolds.Add(id);
            }
        }

        RebuildCurrentDiff();
        OnFoldsChanged();
    }

    partial void OnFoldsChanged() => UpdateMarkReviewed();

    // Right pane -----------------------------------------------------------------------------------

    /// <summary>Collapsible; the view starts it closed below about 1400px wide. Flags stay reachable from the slice header.</summary>
    [ObservableProperty]
    public partial bool IsRightPaneOpen { get; set; } = true;

    [RelayCommand]
    public void ToggleRightPane() => IsRightPaneOpen = !IsRightPaneOpen;

    /// <summary>Slices or the file tree (T).</summary>
    [RelayCommand]
    public void ToggleFileList()
    {
        ShowFileList = HasSlices && !ShowFileList;
        ShowTab(PullRequestTab.Files);
    }

    // Red flags ------------------------------------------------------------------------------------

    public ObservableCollection<FlagViewModel> Flags { get; } = [];

    /// <summary>Flags fixed since the last look: no longer in the code, listed so the reviewer sees what went.</summary>
    public ObservableCollection<FlagViewModel> FixedFlags { get; } = [];

    [ObservableProperty]
    public partial FlagViewModel? CurrentFlag { get; private set; }

    public IReadOnlyList<FlagViewModel> CurrentSliceFlags => CurrentSlice is { } slice
        ? [.. FlagOrder().Where(f => slice.Files.Any(sf => sf.Path == f.Path))]
        : [.. FlagOrder()];

    public int CurrentSliceFlagCount => CurrentSliceFlags.Count;

    public bool HasFlags => Flags.Count > 0;

    partial void ApplyFlags(FlagsReady flags)
    {
        Flags.ReplaceWith(flags.Flags.Select(f => new FlagViewModel(f)));
        OnPropertyChanged(nameof(HasFlags));
        ApplyFlagChanges();
        RecountSlices();
        RebuildCurrentDiff();
    }

    /// <summary>Flags in reading order: the slice order, then each file's order, then the line.</summary>
    private IEnumerable<FlagViewModel> FlagOrder()
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        var i = 0;
        foreach (var file in SliceOrder().SelectMany(s => s.Files))
        {
            rank.TryAdd(file.Path, i++);
        }

        return Flags.OrderBy(f => rank.GetValueOrDefault(f.Path, int.MaxValue)).ThenBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Line);
    }

    private IEnumerable<CardAnchor> FlagAnchors(string path) =>
        Flags.Where(f => f.Path == path).Select(f => new CardAnchor(FlagCardPrefix + f.Id, f.IsOldSide ? LineSide.Left : LineSide.Right, f.Line));

    private IEnumerable<GutterMarker> FlagMarkers(string path) =>
        Flags.Where(f => f.Path == path && !f.IsOldSide).Select(f => new GutterMarker(f.Id, f.Line, "flag", f == CurrentFlag, false, false));

    public FlagCardContext? ResolveFlagCard(string cardId) =>
        cardId.StartsWith(FlagCardPrefix, StringComparison.Ordinal) && Flags.FirstOrDefault(f => f.Id == cardId[FlagCardPrefix.Length..]) is { } flag
            ? new FlagCardContext(this, flag)
            : null;

    [RelayCommand]
    public async Task GoToFlagAsync(string? id)
    {
        if (Flags.FirstOrDefault(f => f.Id == id) is not { } flag)
        {
            return;
        }

        CurrentFlag = flag;
        if (Files.FirstOrDefault(f => f.Path == flag.Path) is { } file && SelectedFile?.Path != flag.Path)
        {
            await SelectFileAsync(file).ConfigureAwait(true);
        }

        UpdateMarkers();
        FocusRange = (flag.Line, flag.Line);
        LineFocusRequested?.Invoke(this, (flag.Line, flag.IsOldSide));
    }

    [RelayCommand]
    public Task SelectFlagAsync(FlagViewModel? flag) => GoToFlagAsync(flag?.Id);

    /// <summary>Next flag in reading order (G); Shift+G goes back.</summary>
    [RelayCommand]
    public Task NextFlagAsync() => StepFlagAsync(+1);

    [RelayCommand]
    public Task PreviousFlagAsync() => StepFlagAsync(-1);

    private Task StepFlagAsync(int direction)
    {
        var order = FlagOrder().ToList();
        if (order.Count == 0)
        {
            Toast = "No red flags in this change.";
            return Task.CompletedTask;
        }

        var index = CurrentFlag is null ? -1 : order.IndexOf(CurrentFlag);
        var next = index < 0 ? (direction > 0 ? 0 : order.Count - 1) : (index + direction + order.Count) % order.Count;
        return GoToFlagAsync(order[next].Id);
    }

    // Marks ----------------------------------------------------------------------------------------

    partial void ApplyMarks(MarksUpdated marks)
    {
        _marks.Clear();
        foreach (var mark in marks.Marks)
        {
            _marks[mark.Unit] = mark.Fingerprint;
        }

        foreach (var path in marks.ViewedPaths)
        {
            if (Files.FirstOrDefault(f => f.Path == path) is { } file)
            {
                file.IsViewed = true;
            }
        }

        if (marks.ViewedPaths.Count > 0)
        {
            OnPropertyChanged(nameof(ViewedSummary));
            OnPropertyChanged(nameof(ViewedCount));
        }

        RecomputeReviewed();
    }

    /// <summary>A slice is reviewed when every unit in it is marked at its current fingerprint.</summary>
    private void RecomputeReviewed()
    {
        foreach (var slice in _slices)
        {
            var units = slice.Units;
            slice.IsReviewed = units.Count > 0 && units.All(IsUnitReviewed);
            slice.ChangedSinceMark = !slice.IsReviewed && units.Any(u => _marks.TryGetValue(u, out var fp) && fp != Fingerprint(u));
            foreach (var file in slice.Files)
            {
                file.IsMarked = units.Where(u => PathOf(u) == file.Path).All(IsUnitReviewed);
            }
        }

        var counted = _slices.Where(s => !s.IsMechanicalOnly).ToList();
        ReviewedSliceCount = counted.Count(s => s.IsReviewed);
        var left = counted.Where(s => !s.IsReviewed).Sum(s => s.OpenLines);
        var changed = counted.Count(s => s.ChangedSinceMark);
        SliceProgressLabel = counted.Count == 0
            ? ""
            : string.Create(CultureInfo.InvariantCulture, $"{ReviewedSliceCount} of {counted.Count} slices reviewed · {left:N0} lines left")
              + (changed > 0 ? string.Create(CultureInfo.InvariantCulture, $" · {changed} changed since you marked {(changed == 1 ? "it" : "them")}") : "");
        UpdateMarkReviewed();
    }

    private string Fingerprint(string unit) => _units.TryGetValue(unit, out var fp) ? fp.Fingerprint : "";

    private bool IsUnitReviewed(string unit) => _marks.TryGetValue(unit, out var fp) && fp == Fingerprint(unit);

    private void UpdateMarkReviewed()
    {
        if (CurrentSlice is not { } slice)
        {
            MarkReviewed.IsReviewed = false;
            MarkReviewed.OpenFlags = 0;
            MarkReviewed.UnopenedFolds = 0;
            return;
        }

        MarkReviewed.IsReviewed = slice.IsReviewed;
        MarkReviewed.OpenFlags = slice.FlagCount;
        var paths = slice.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

        // Summarised (boilerplate) folds count; proven-mechanical ones are proven, so nobody has to open them.
        MarkReviewed.UnopenedFolds = Triage?.Hunks.Count(h => paths.Contains(h.Path) && h.Fold == FoldKind.Boilerplate && !_openedFolds.Contains(DiffFolds.IdFor(h))) ?? 0;
    }

    /// <summary>
    /// Marks the current slice reviewed (M). With open flags or summarised folds nobody opened, the first press says so
    /// and a second press confirms. On a reviewed slice it takes the mark back.
    /// </summary>
    [RelayCommand]
    public async Task MarkSliceReviewedAsync()
    {
        if (CurrentSlice is not { } slice || slice.Units.Count == 0)
        {
            return;
        }

        if (slice.IsReviewed)
        {
            await SetMarkedAsync(slice.Units, marked: false).ConfigureAwait(true);
            return;
        }

        UpdateMarkReviewed();
        if (MarkReviewed.NeedsConfirmation && !MarkReviewed.IsArmed)
        {
            MarkReviewed.IsArmed = true;
            return;
        }

        MarkReviewed.IsArmed = false;
        await SetMarkedAsync(slice.Units, marked: true).ConfigureAwait(true);
        if (slice.IsReviewed && SliceOrder().SkipWhile(s => s != slice).Skip(1).FirstOrDefault(s => !s.IsReviewed && !s.IsMechanicalOnly) is { } next)
        {
            await SelectSliceAsync(next).ConfigureAwait(true);
        }
    }

    private async Task SetMarkedAsync(IEnumerable<string> units, bool marked)
    {
        var request = new SetMarkedRequest { PullRequest = Ref, Marked = marked };
        request.Units.AddRange(units);
        try
        {
            ApplyMarks(await _source.SetMarkedAsync(request, _lifetime.Token).ConfigureAwait(true));
        }
        catch (RpcException ex) when (!(ex.StatusCode == StatusCode.Cancelled && _lifetime.IsCancellationRequested))
        {
            Toast = $"Couldn't save the mark: {ex.Status.Detail}";
        }
        catch (EngineUnavailableException ex)
        {
            Toast = $"Couldn't save the mark: {ex.Message}";
        }
    }

    // Since my last look ---------------------------------------------------------------------------

    /// <summary>The slice list shows only slices that changed since the last look, each cut down to the range diff.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllSlices))]
    public partial bool ShowSinceLastLook { get; set; }

    public bool IsAllSlices => !ShowSinceLastLook;

    [ObservableProperty]
    public partial string SinceLookLabel { get; private set; } = "";

    [ObservableProperty]
    public partial string? SinceLookNote { get; private set; }

    [ObservableProperty]
    public partial bool IsHistoryRewritten { get; private set; }

    [ObservableProperty]
    public partial string SinceLookSummary { get; private set; } = "";

    [RelayCommand]
    public void ShowAllSlices() => ShowSinceLastLook = false;

    [RelayCommand]
    public void ToggleSinceLastLook() => ShowSinceLastLook = !ShowSinceLastLook;

    partial void OnShowSinceLastLookChanged(bool value) => _ = ApplySinceLastLookAsync(value);

    private async Task ApplySinceLastLookAsync(bool on)
    {
        if (!on)
        {
            _sinceLook = null;
            foreach (var slice in _slices)
            {
                slice.SinceLines = 0;
            }

            foreach (var flag in Flags)
            {
                flag.ChangeLabel = "";
            }

            FixedFlags.Clear();
            SinceLookLabel = "";
            SinceLookNote = null;
            IsHistoryRewritten = false;
            SinceLookSummary = "";
            RebuildSliceGroups();
            if (Scope?.Key.StartsWith("since:", StringComparison.Ordinal) == true)
            {
                await ClearScopeAsync().ConfigureAwait(true);
            }

            return;
        }

        try
        {
            var since = await _source.GetSinceLastLookAsync(Ref, _lifetime.Token).ConfigureAwait(true);
            ApplySinceLastLook(since);
        }
        catch (RpcException ex) when (!(ex.StatusCode == StatusCode.Cancelled && _lifetime.IsCancellationRequested))
        {
            Toast = $"Couldn't compare with your last look: {ex.Status.Detail}";
            ShowSinceLastLook = false;
        }
        catch (EngineUnavailableException ex)
        {
            Toast = ex.Message;
            ShowSinceLastLook = false;
        }
    }

    internal void ApplySinceLastLook(SinceLastLook since)
    {
        _sinceLook = since;
        IsHistoryRewritten = since.HistoryRewritten;
        SinceLookNote = since.Note.Length > 0 ? since.Note : null;
        if (since.OldHeadSha.Length == 0)
        {
            SinceLookLabel = "You haven't looked at this change before";
        }
        else
        {
            SinceLookLabel = $"Since your last look · {Short(since.OldHeadSha)} → {Short(since.NewHeadSha)}";
        }

        var lines = since.Files.ToDictionary(f => f.Path, f => f.Hunks.Sum(h => h.Lines.Count(l => l.Kind != DiffLineKind.Context)), StringComparer.Ordinal);
        foreach (var slice in _slices)
        {
            slice.SinceLines = slice.Files.Sum(f => lines.GetValueOrDefault(f.Path));
        }

        var changedSlices = _slices.Count(s => s.SinceLines > 0 && !s.IsMechanicalOnly);
        var unchanged = _slices.Count(s => s.SinceLines == 0 && !s.IsMechanicalOnly);
        SinceLookSummary = string.Create(CultureInfo.InvariantCulture,
            $"{changedSlices} slice{(changedSlices == 1 ? "" : "s")} changed · {lines.Values.Sum():N0} lines to read · {unchanged} unchanged");
        ApplyFlagChanges();
        RebuildSliceGroups();

        // The diff shows each file reduced to its range diff, from the engine's per-file comparison.
        var key = $"since:{since.OldHeadSha}..{since.NewHeadSha}";
        var scope = new DiffScope(since.OldHeadSha, since.NewHeadSha, SinceLookLabel, IsSinceLastReview: true) { Note = SinceLookNote, KeyOverride = key };
        foreach (var file in since.Files)
        {
            _diffCache[$"{key}|{(IgnoreWhitespace ? "w" : "")}|{file.Path}"] = file;
        }

        ScopeFiles.ReplaceWith(since.Files
            .Select(f => Files.FirstOrDefault(e => e.Path == f.Path))
            .OfType<FileEntryViewModel>());
        _applyingScope = true;
        try
        {
            Scope = scope;
        }
        finally
        {
            _applyingScope = false;
        }

        RebuildTree();
        if (SliceOrder().FirstOrDefault() is { } first)
        {
            _ = SelectSliceAsync(first);
        }

        if (ScopeFiles.FirstOrDefault() is { } firstFile)
        {
            _ = SelectFileAsync(firstFile);
        }
    }

    private void ApplyFlagChanges()
    {
        FixedFlags.Clear();
        if (_sinceLook is not { } since || since.OldHeadSha.Length == 0)
        {
            foreach (var flag in Flags)
            {
                flag.ChangeLabel = "";
            }

            return;
        }

        var added = since.NewFlagIds.ToHashSet(StringComparer.Ordinal);
        foreach (var flag in Flags)
        {
            flag.ChangeLabel = added.Contains(flag.Id) ? "New" : "";
        }

        FixedFlags.ReplaceWith(since.FixedFlags.Select(f => new FlagViewModel(f) { ChangeLabel = "Fixed" }));
    }

    // Comment (C) ----------------------------------------------------------------------------------

    /// <summary>Starts a comment on the last line of the current change (C), on the side that change is on.</summary>
    [RelayCommand]
    public void CommentOnCurrentChange()
    {
        if (CurrentDiff is not { Changes.Count: > 0 } diff)
        {
            return;
        }

        var change = diff.Changes[Math.Clamp(_currentChange, 0, diff.Changes.Count - 1)];
        var rows = diff.Rows.Skip(change.StartLine - 1).Take(change.EndLine - change.StartLine + 1).ToList();
        if (rows.LastOrDefault(r => r.NewNumber > 0) is { } added)
        {
            var first = rows.FirstOrDefault(r => r.NewNumber > 0)?.NewNumber;
            StartLineComment(LineSide.Right, added.NewNumber, first is { } f && f != added.NewNumber ? f : null);
        }
        else if (rows.LastOrDefault(r => r.OldNumber > 0) is { } removed)
        {
            StartLineComment(LineSide.Left, removed.OldNumber);
        }
    }

    partial void ResetOverhaulState()
    {
        StopSlicing();
        _slices.Clear();
        _units.Clear();
        _marks.Clear();
        _sinceLook = null;
        _openedFolds.Clear();
        SliceGroups.Clear();
        HasSlices = false;
        SliceFallbackReason = null;
        CurrentSlice = null;
        Flags.Clear();
        FixedFlags.Clear();
        CurrentFlag = null;
        SliceProgressLabel = "";
        ShowSinceLastLook = false;
        OnPropertyChanged(nameof(HasFlags));
    }
}
