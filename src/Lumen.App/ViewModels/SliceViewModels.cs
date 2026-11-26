using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

/// <summary>A heading in the slice list: "Pages", "Across slices", "Nothing to read".</summary>
public sealed class SliceGroupViewModel(string title, IReadOnlyList<SliceViewModel> slices)
{
    public string Title { get; } = title;

    public IReadOnlyList<SliceViewModel> Slices { get; } = slices;
}

/// <summary>A link with its reason in words ("page loads `orders-edit.js`"), shown under "Why these files are together".</summary>
public sealed class SliceEdgeViewModel(SliceEdge edge)
{
    public SliceEdge Model { get; } = edge;

    public string From => FileName(Model.From);

    public string To => FileName(Model.To);

    public string Reason => Model.Reason;

    public bool IsPossible => Model.Possible;

    private static string FileName(string node)
    {
        var path = node.Split("::", 2)[0];
        var name = path[(path.LastIndexOf('/') + 1)..];
        return node.Contains("::", StringComparison.Ordinal) ? $"{name} · {node.Split("::", 2)[1]}" : name;
    }
}

/// <summary>One file in a slice, in reading order, with why it is there.</summary>
public sealed partial class SliceFileViewModel(SliceFile file, FileEntryViewModel? entry, int position) : ObservableObject
{
    public SliceFile Model { get; } = file;

    /// <summary>The changed file this entry stands for; null when the snapshot didn't include it.</summary>
    public FileEntryViewModel? File { get; } = entry;

    public string Path => Model.Path;

    public string Name => Model.Path[(Model.Path.LastIndexOf('/') + 1)..];

    public string Directory => Model.Path.Contains('/', StringComparison.Ordinal) ? Model.Path[..Model.Path.LastIndexOf('/')] : "";

    /// <summary>1-based place in the reading order.</summary>
    public int Position { get; } = position;

    public string PositionLabel => Position.ToString(CultureInfo.InvariantCulture);

    /// <summary>"injected into EditModel", "page loads orders-edit.js"…</summary>
    public string Why => Model.Why;

    public string Section => Model.Section;

    public bool HasSection => Model.Section.Length > 0;

    public string OutsideCallersLabel => Model.OutsideCallers switch
    {
        0 => "nothing uses it",
        1 => "used by 1 unchanged file",
        var n => string.Create(CultureInfo.InvariantCulture, $"used by {n} unchanged files"),
    };

    [ObservableProperty]
    public partial int OpenLines { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFlags), nameof(FlagLabel))]
    public partial int FlagCount { get; set; }

    public bool HasFlags => FlagCount > 0;

    public string FlagLabel => FlagCount == 1 ? "1 flag" : string.Create(CultureInfo.InvariantCulture, $"{FlagCount} flags");

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    public partial bool IsMarked { get; set; }
}

/// <summary>
/// A feature slice (or Shared, Unlinked, Mechanical only): the code behind one entry point, its files in reading
/// order, its flags and whether every unit in it is marked reviewed.
/// </summary>
public sealed partial class SliceViewModel : ObservableObject
{
    public SliceViewModel(Slice slice, IReadOnlyList<SliceFileViewModel> files, IReadOnlyList<SliceEdgeViewModel> edges)
    {
        Model = slice;
        Files = files;
        Edges = edges;
        PossibleLinks = [.. slice.PossibleLinks.Select(e => new SliceEdgeViewModel(e))];
        PageLinks = [.. slice.PageLinks.Select(e => new SliceEdgeViewModel(e))];
    }

    public Slice Model { get; }

    public string Id => Model.Id;

    public string Title => Model.Title;

    public SliceKind Kind => Model.Kind;

    public bool IsFeature => Kind == SliceKind.Feature;

    public bool IsShared => Kind == SliceKind.Shared;

    public bool IsUnlinked => Kind is SliceKind.UnlinkedUsed or SliceKind.UnlinkedUnused;

    public bool IsMechanicalOnly => Kind == SliceKind.MechanicalOnly;

    public IReadOnlyList<SliceFileViewModel> Files { get; }

    /// <summary>Membership edges inside the slice: "Why these files are together".</summary>
    public IReadOnlyList<SliceEdgeViewModel> Edges { get; }

    public IReadOnlyList<SliceEdgeViewModel> PossibleLinks { get; }

    public IReadOnlyList<SliceEdgeViewModel> PageLinks { get; }

    public bool HasPossibleLinks => PossibleLinks.Count > 0;

    public bool HasPageLinks => PageLinks.Count > 0;

    public IReadOnlyList<string> Sections => Model.Sections;

    public bool HasSections => Model.Sections.Count > 0;

    /// <summary>"handlers OnGetAsync, OnPostSaveAsync".</summary>
    public string SectionsLabel => HasSections ? "handlers " + string.Join(", ", Model.Sections) : "";

    public string SeedPath => Model.SeedPath;

    public string FilesLabel => Files.Count == 1 ? "1 file" : string.Create(CultureInfo.InvariantCulture, $"{Files.Count} files");

    /// <summary>"Order.cs › OrderEditInput.cs › OrderService.cs › …".</summary>
    public string ReadingOrderLabel => string.Join(" › ", Files.Select(f => f.Name));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinesLabel))]
    public partial int OpenLines { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinesLabel))]
    public partial int ChangedLines { get; set; }

    public int BoilerplateLines { get; set; }

    public int MechanicalLines { get; set; }

    /// <summary>"412 to read of 1,306", or "88 to read" when nothing folds.</summary>
    public string LinesLabel => IsMechanicalOnly
        ? string.Create(CultureInfo.InvariantCulture, $"{MechanicalLines:N0} lines, all proven")
        : ChangedLines > OpenLines
            ? string.Create(CultureInfo.InvariantCulture, $"{OpenLines:N0} to read of {ChangedLines:N0}")
            : string.Create(CultureInfo.InvariantCulture, $"{OpenLines:N0} to read");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFlags), nameof(FlagLabel), nameof(StateLabel))]
    public partial int FlagCount { get; set; }

    public bool HasFlags => FlagCount > 0;

    public string FlagLabel => FlagCount == 1 ? "1 flag" : string.Create(CultureInfo.InvariantCulture, $"{FlagCount} flags");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(StateKind))]
    public partial bool IsReviewed { get; set; }

    /// <summary>Some unit was marked, then changed by a push: the mark no longer counts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(StateKind))]
    public partial bool ChangedSinceMark { get; set; }

    /// <summary>"✓ Reviewed", "Changed after review", "Not reviewed".</summary>
    public string StateLabel => IsReviewed ? "✓ Reviewed" : ChangedSinceMark ? "Changed after review" : "Not reviewed";

    /// <summary>"reviewed", "changed" or "open", for styling.</summary>
    public string StateKind => IsReviewed ? "reviewed" : ChangedSinceMark ? "changed" : "open";

    /// <summary>For Unlinked: how its files fail to connect.</summary>
    public string Caption => Kind switch
    {
        SliceKind.Shared => "used by three or more slices",
        SliceKind.UnlinkedUsed => "used only by code outside this change",
        SliceKind.UnlinkedUnused => "nothing in this change uses it",
        SliceKind.MechanicalOnly => "renames, moves, formatting, imports, generated",
        _ => Model.SeedPath,
    };

    /// <summary>The units (file paths, or path::member) whose marks decide whether this slice is reviewed.</summary>
    public List<string> Units { get; } = [];

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>Since-my-last-look: lines in the range diff for this slice.</summary>
    [ObservableProperty]
    public partial int SinceLines { get; set; }
}

/// <summary>A red flag: a deterministic fact, shown on its line and counted on its slice. Never a score.</summary>
public sealed partial class FlagViewModel(RedFlag flag) : ObservableObject
{
    public RedFlag Model { get; } = flag;

    public string Id => Model.Id;

    public string Path => Model.Path;

    public int Line => Model.Line;

    public bool IsOldSide => Model.Side == DiffSide.Left;

    public string Message => Model.Message;

    public string FileName => Model.Path[(Model.Path.LastIndexOf('/') + 1)..];

    public string LocationLabel => string.Create(CultureInfo.InvariantCulture, $"{FileName}:{Model.Line}");

    /// <summary>"Antiforgery switched off", "Guard removed"…</summary>
    public string KindLabel => Label(Model.Kind);

    public static string Label(FlagKind kind) => kind switch
    {
        FlagKind.WeakenedTests => "Tests weakened",
        FlagKind.AntiforgeryOff => "Antiforgery switched off",
        FlagKind.AuthorisationLoosened => "Authorisation loosened",
        FlagKind.OverPosting => "Over-posting",
        FlagKind.GuardRemoved => "Guard or validation removed",
        FlagKind.RawOutput => "Raw output",
        FlagKind.RawSql => "Raw SQL",
        FlagKind.QuerySafety => "Query filters bypassed",
        FlagKind.SwallowedErrors => "Error swallowed",
        FlagKind.WarningsSilenced => "Warnings silenced",
        FlagKind.DependenciesAndBuild => "Dependencies and build",
        FlagKind.SecurityConfig => "Security config changed",
        _ => "Flag",
    };

    /// <summary>Since-my-last-look: "New" or "Fixed" (a fixed flag is no longer in the code).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChangeLabel))]
    public partial string ChangeLabel { get; set; } = "";

    public bool HasChangeLabel => ChangeLabel.Length > 0;
}

/// <summary>A flag's card under its line in the diff.</summary>
public sealed class FlagCardContext(PullRequestViewModel owner, FlagViewModel flag)
{
    public PullRequestViewModel Owner { get; } = owner;

    public FlagViewModel Flag { get; } = flag;
}

/// <summary>"Mark reviewed" for one slice: says what's still open before it marks, and needs a second press then.</summary>
public sealed partial class MarkReviewedViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(Hint), nameof(HasHint))]
    public partial bool IsArmed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(Hint), nameof(HasHint))]
    public partial int OpenFlags { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(Hint), nameof(HasHint))]
    public partial int UnopenedFolds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    public partial bool IsReviewed { get; set; }

    public bool NeedsConfirmation => OpenFlags + UnopenedFolds > 0;

    public string Label => IsReviewed ? "Reviewed ✓ · undo" : IsArmed ? "Press again to mark reviewed" : "Mark reviewed";

    /// <summary>"3 flags, 2 folds not opened".</summary>
    public string Hint
    {
        get
        {
            var parts = new List<string>();
            if (OpenFlags > 0)
            {
                parts.Add(OpenFlags == 1 ? "1 flag" : string.Create(CultureInfo.InvariantCulture, $"{OpenFlags} flags"));
            }

            if (UnopenedFolds > 0)
            {
                parts.Add(UnopenedFolds == 1 ? "1 fold not opened" : string.Create(CultureInfo.InvariantCulture, $"{UnopenedFolds} folds not opened"));
            }

            return string.Join(", ", parts);
        }
    }

    public bool HasHint => !IsReviewed && Hint.Length > 0;
}

/// <summary>The inbox's local branch row.</summary>
public sealed class LocalBranchItemViewModel(LocalBranchItem item, Func<LocalBranchRef, Task> open, DateTimeOffset now)
{
    public LocalBranchItem Model { get; } = item;

    public string Title => Model.Ref.WorkingTree ? "Working tree" : Model.Ref.HeadRef;

    public string Detail => $"{Model.Ref.ClonePath} · against {Model.Ref.BaseRef}";

    public string WhenLabel => Model.LastOpenedAt is null ? "" : InboxItemViewModel.Age(Model.LastOpenedAt.ToDateTimeOffset(), now);

    public ReviewProgress Progress => Model.Progress ?? new ReviewProgress();

    public string ShrinkLabel => Progress.Analysed ? ProgressText.Shrink(Progress) : "Not analysed yet";

    public string SlicesLabel => ProgressText.Slices(Progress);

    public Task OpenAsync() => open(Model.Ref);
}

/// <summary>A collection helper: replaces everything in one go.</summary>
internal static class CollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
