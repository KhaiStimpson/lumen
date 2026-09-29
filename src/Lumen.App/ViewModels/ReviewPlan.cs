using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

/// <summary>A span of the head (or, for pure deletions, the base) that a plan item points at.</summary>
public sealed record PlanTarget(string Path, int StartLine, int EndLine);

/// <summary>A triage group in the plan, e.g. "Renamed `Foo`→`Bar`" across 12 hunks. Acknowledging clears all of it.</summary>
public sealed partial class TriageGroupViewModel : ObservableObject
{
    public TriageGroupViewModel(TriageGroup group, IReadOnlyList<HunkTriage> members)
    {
        Id = group.Id;
        Update(group, members);
    }

    public string Id { get; }

    public string Title { get; private set; } = "";

    public TriageTier Tier { get; private set; }

    public IReadOnlyList<HunkTriage> Members { get; private set; } = [];

    public string Caption { get; private set; } = "";

    public PlanTarget? Target => Members.Count == 0 ? null : ReviewPlanBuilder.TargetOf(Members[0]);

    [ObservableProperty]
    public partial bool Acknowledged { get; set; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    internal void Update(TriageGroup group, IReadOnlyList<HunkTriage> members)
    {
        Title = group.Title;
        Members = members;
        Acknowledged = group.Acknowledged;

        // A group sits in the tier of its most demanding member (a ripple's call sites are Skim, a rename is Skip).
        Tier = members.Count == 0 ? TriageTier.Skip : members.Select(m => m.Tier).OrderBy(ReviewPlanBuilder.Rank).First();
        var files = members.Select(m => m.Path).Distinct(StringComparer.Ordinal).Count();
        var lines = members.Sum(m => m.ChangedLines);
        Caption = string.Create(CultureInfo.InvariantCulture,
            $"{Plural(members.Count, "hunk")} in {Plural(files, "file")} · {Plural(lines, "line")}");
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Tier));
        OnPropertyChanged(nameof(Caption));
    }

    internal static string Plural(int n, string word) =>
        string.Create(CultureInfo.InvariantCulture, $"{n:N0} {word}{(n == 1 ? "" : "s")}");
}

/// <summary>Hunks in a tier that no review point or group covers, folded into one row per tier.</summary>
public sealed class MoreChangesViewModel(TriageTier tier, IReadOnlyList<HunkTriage> hunks, bool afterOtherItems = false)
{
    public TriageTier Tier { get; } = tier;

    public IReadOnlyList<HunkTriage> Hunks { get; } = hunks;

    public string Label
    {
        get
        {
            var files = Hunks.Select(h => h.Path).Distinct(StringComparer.Ordinal).Count();
            var lines = Hunks.Sum(h => h.ChangedLines);
            var what = afterOtherItems ? "more " : "";
            return $"{TriageGroupViewModel.Plural(Hunks.Count, what + "change")} in {TriageGroupViewModel.Plural(files, "file")} · {TriageGroupViewModel.Plural(lines, "line")}";
        }
    }

    /// <summary>The most common reason in these hunks, so the row says what they are ("new file", "usings added…").</summary>
    public string Reason => Hunks
        .Select(h => h.Reasons.FirstOrDefault() ?? "")
        .Where(r => r.Length > 0)
        .GroupBy(r => r.StartsWith("usings", StringComparison.Ordinal) ? "usings only" : r, StringComparer.Ordinal)
        .OrderByDescending(g => g.Count())
        .Select(g => g.Key)
        .FirstOrDefault() ?? "";

    public PlanTarget? Target => Hunks.Count == 0 ? null : ReviewPlanBuilder.TargetOf(Hunks[0]);
}

/// <summary>One tier of the plan (or the Consistency notes): a header and its rows.</summary>
public sealed partial class PlanSectionViewModel : ObservableObject
{
    public PlanSectionViewModel(string key, string title, string pillClass, bool isFoldable, bool isExpanded)
    {
        Key = key;
        Title = title;
        PillClass = pillClass;
        IsFoldable = isFoldable;
        IsExpanded = isExpanded;
    }

    /// <summary>"Critical", "WorthALook", "Skim", "Skip" or "Consistency".</summary>
    public string Key { get; }

    public string Title { get; }

    /// <summary>The pill style class: High, Medium, Low, or empty for the plain pill.</summary>
    public string PillClass { get; }

    public bool IsCritical => PillClass == "High";

    public bool IsWorthALook => PillClass == "Medium";

    public bool IsSkim => PillClass == "Low";

    public bool IsFoldable { get; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial string CountLabel { get; set; } = "";

    /// <summary>ReviewPointViewModel, TriageGroupViewModel or MoreChangesViewModel rows, in reading order.</summary>
    public ObservableCollection<object> Items { get; } = [];

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>What the right pane shows: a summary in words, lines per tier, and the tiers the reviewer works through.</summary>
public sealed partial class ReviewPlanViewModel : ObservableObject
{
    private readonly Dictionary<string, TriageGroupViewModel> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _expanded = new(StringComparer.Ordinal);

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial bool HasTriage { get; set; }

    /// <summary>Changed lines per tier, for the tier bar: Critical, Worth a look, Skim, Skip.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<double> TierLines { get; set; } = [0, 0, 0, 0];

    [ObservableProperty]
    public partial string ItemsLabel { get; set; } = "";

    public ObservableCollection<PlanSectionViewModel> Sections { get; } = [];

    public IEnumerable<TriageGroupViewModel> Groups => _groups.Values;

    /// <summary>Rebuilds the sections, keeping group and fold state across rebuilds.</summary>
    public void Rebuild(TriageReady? triage, IReadOnlyList<ReviewPointViewModel> points)
    {
        foreach (var section in Sections)
        {
            _expanded[section.Key] = section.IsExpanded;
        }

        var plan = ReviewPlanBuilder.Build(triage, points, _groups);
        Sections.Clear();
        foreach (var section in plan.Sections)
        {
            if (_expanded.TryGetValue(section.Key, out var expanded))
            {
                section.IsExpanded = expanded;
            }

            Sections.Add(section);
        }

        HasTriage = triage is not null;
        Summary = triage?.Summary?.Text ?? "";
        var tiers = triage?.Summary?.TierLines;
        TierLines = [tiers?.Critical ?? 0, tiers?.WorthALook ?? 0, tiers?.Skim ?? 0, tiers?.Skip ?? 0];
        ItemsLabel = plan.ItemsLabel;
    }
}

public sealed record ReviewPlan(IReadOnlyList<PlanSectionViewModel> Sections, string ItemsLabel);

/// <summary>Places review points, groups and the remaining hunks into tiers. Pure, so it is tested without a UI.</summary>
public static class ReviewPlanBuilder
{
    private static readonly (TriageTier Tier, string Key, string Title, string Pill, bool Foldable, bool Expanded)[] Tiers =
    [
        (TriageTier.Critical, "Critical", "Critical", "High", false, true),
        (TriageTier.WorthALook, "WorthALook", "Worth a look", "Medium", false, true),
        (TriageTier.Skim, "Skim", "Skim", "Low", false, true),
        (TriageTier.Skip, "Skip", "Skip", "", true, false),
    ];

    public static int Rank(TriageTier tier) => tier switch
    {
        TriageTier.Critical => 0,
        TriageTier.WorthALook => 1,
        TriageTier.Skim => 2,
        _ => 3,
    };

    /// <summary>Peer-pattern notes answer "what is unusual", not "what is risky": they never outrank a tier.</summary>
    public static bool IsConsistencyNote(ReviewPointViewModel point) => point.Kind == "PatternDeviation";

    public static PlanTarget TargetOf(HunkTriage hunk) => hunk.NewStart > 0
        ? new PlanTarget(hunk.Path, hunk.NewStart, Math.Max(hunk.NewStart, hunk.NewEnd))
        : new PlanTarget(hunk.Path, hunk.OldStart, Math.Max(hunk.OldStart, hunk.OldEnd));

    /// <summary>The tier of the hunk a point is anchored in; else the file's most demanding hunk; else Worth a look.</summary>
    public static TriageTier TierOf(ReviewPointViewModel point, TriageReady? triage)
    {
        if (triage is null)
        {
            return TriageTier.WorthALook;
        }

        var inFile = triage.Hunks.Where(h => h.Path == point.Path).ToList();
        var covering = inFile.Where(h => h.NewStart > 0 && h.NewStart <= point.Line && point.Line <= h.NewEnd).ToList();
        var candidates = covering.Count > 0 ? covering : inFile;
        return candidates.Count == 0 ? TriageTier.WorthALook : candidates.Select(h => h.Tier).OrderBy(Rank).First();
    }

    public static ReviewPlan Build(TriageReady? triage, IReadOnlyList<ReviewPointViewModel> points, Dictionary<string, TriageGroupViewModel> groupState)
    {
        // Groups: reuse view models so acknowledgement and the current marker survive a rebuild.
        var groups = new List<TriageGroupViewModel>();
        foreach (var group in triage?.Groups ?? [])
        {
            var members = triage!.Hunks.Where(h => h.GroupId == group.Id).ToList();
            if (groupState.TryGetValue(group.Id, out var existing))
            {
                existing.Update(group, members);
            }
            else
            {
                groupState[group.Id] = existing = new TriageGroupViewModel(group, members);
            }

            groups.Add(existing);
        }

        var risk = points.Where(p => !IsConsistencyNote(p)).ToList();
        var anchored = risk.Where(p => !p.IsDismissed).Select(p => (p.Path, p.Line)).ToList();

        var sections = new List<PlanSectionViewModel>();
        var itemCount = 0;
        foreach (var (tier, key, title, pill, foldable, expanded) in Tiers)
        {
            var section = new PlanSectionViewModel(key, title, pill, foldable, expanded);
            foreach (var point in risk.Where(p => TierOf(p, triage) == tier))
            {
                section.Items.Add(point);
            }

            foreach (var group in groups.Where(g => g.Tier == tier).OrderByDescending(g => g.Members.Sum(m => m.ChangedLines)))
            {
                section.Items.Add(group);
            }

            var loose = (triage?.Hunks ?? [])
                .Where(h => h.Tier == tier && h.GroupId.Length == 0 && !anchored.Any(a => a.Path == h.Path && h.NewStart > 0 && h.NewStart <= a.Line && a.Line <= h.NewEnd))
                .ToList();
            if (loose.Count > 0)
            {
                section.Items.Add(new MoreChangesViewModel(tier, loose, afterOtherItems: section.Items.Count > 0));
            }

            var lines = (triage?.Hunks ?? []).Where(h => h.Tier == tier).Sum(h => h.ChangedLines);
            section.CountLabel = lines > 0 ? TriageGroupViewModel.Plural(lines, "line") : "";

            if (section.Items.Count == 0)
            {
                continue;
            }

            if (tier is TriageTier.Critical or TriageTier.WorthALook)
            {
                itemCount += section.Items.Sum(i => i switch
                {
                    ReviewPointViewModel p => p.IsDismissed ? 0 : 1,
                    TriageGroupViewModel g => g.Acknowledged ? 0 : 1,
                    MoreChangesViewModel m => m.Hunks.Count,
                    _ => 0,
                });
            }

            sections.Add(section);
        }

        var notes = points.Where(IsConsistencyNote).ToList();
        if (notes.Count > 0)
        {
            var consistency = new PlanSectionViewModel("Consistency", "Consistency", "", isFoldable: true, isExpanded: false)
            {
                CountLabel = TriageGroupViewModel.Plural(notes.Count, "note"),
            };
            foreach (var note in notes)
            {
                consistency.Items.Add(note);
            }

            sections.Add(consistency);
        }

        var label = itemCount == 0
            ? "Nothing in Critical or Worth a look"
            : $"{TriageGroupViewModel.Plural(itemCount, "place")} to review";
        return new ReviewPlan(sections, label);
    }
}
