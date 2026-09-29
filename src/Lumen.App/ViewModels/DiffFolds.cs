using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Lumen.App.Diff;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

/// <summary>The inline badge a proven-mechanical hunk collapses behind: the reason, the size, and links to the rest of its group.</summary>
public sealed partial class FoldBadgeViewModel(PullRequestViewModel owner, string id, HunkTriage hunk, bool expanded, TriageGroupViewModel? group, int index)
{
    public string Id { get; } = id;

    /// <summary>In words, e.g. "pure rename `Foo`→`Bar`" or "formatting only".</summary>
    public string Reason { get; } = hunk.Reasons.FirstOrDefault() ?? "mechanical change";

    public string LinesLabel { get; } = TriageGroupViewModel.Plural(hunk.ChangedLines, "line");

    public bool IsExpanded { get; } = expanded;

    public string ToggleLabel => IsExpanded ? "Hide" : "Show";

    public bool HasGroup => group is { Members.Count: > 1 };

    /// <summary>"2 of 12": where this hunk sits in its group; the group's title is the tooltip.</summary>
    public string GroupLabel => group is null ? "" : string.Create(CultureInfo.InvariantCulture, $"{index + 1} of {group.Members.Count}");

    public string GroupTitle => group?.Title ?? "";

    public bool HasPrevious => HasGroup && index > 0;

    public bool HasNext => HasGroup && index < group!.Members.Count - 1;

    [RelayCommand]
    private void Toggle() => owner.ToggleFold(Id);

    [RelayCommand]
    private Task Previous() => group is null ? Task.CompletedTask : owner.GoToGroupMemberAsync(group, index - 1);

    [RelayCommand]
    private Task Next() => group is null ? Task.CompletedTask : owner.GoToGroupMemberAsync(group, index + 1);
}

public static class DiffFolds
{
    public static string IdFor(HunkTriage hunk) =>
        string.Create(CultureInfo.InvariantCulture, $"{hunk.Path}#{hunk.OldStart}:{hunk.NewStart}");

    /// <summary>Hunks a proof put in Skip. Mechanical changes left in Skim (ripple call sites, moves into another type) stay open.</summary>
    public static bool Folds(HunkTriage hunk) =>
        hunk.Tier == TriageTier.Skip && hunk.ChangeClass != ChangeClass.BehaviourChange && hunk.ChangeClass != ChangeClass.NewCode;

    public static IReadOnlyList<DiffFold> For(TriageReady? triage, string path, IReadOnlySet<string> expanded) =>
        triage is null
            ? []
            : [.. triage.Hunks.Where(h => h.Path == path && Folds(h)).Select(h => new DiffFold(IdFor(h), h.OldStart, h.OldEnd, h.NewStart, h.NewEnd, expanded.Contains(IdFor(h))))];
}
