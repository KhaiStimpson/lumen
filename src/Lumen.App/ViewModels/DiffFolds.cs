using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Lumen.App.Diff;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

/// <summary>
/// The one line a folded hunk sits behind: its kind ("Mechanical · Proven" or "Boilerplate · Not verified"), the
/// reason, the size, and links to the rest of its group. Opening it always shows every line.
/// </summary>
public sealed partial class FoldBadgeViewModel(
    PullRequestViewModel owner,
    string id,
    HunkTriage hunk,
    bool expanded,
    TriageGroup? group,
    IReadOnlyList<HunkTriage> members,
    int index)
{
    public string Id { get; } = id;

    public bool IsBoilerplate { get; } = hunk.Fold == FoldKind.Boilerplate;

    public bool IsMechanical => !IsBoilerplate;

    /// <summary>The two kinds are labelled differently: one is proven, the other only matches a listed shape.</summary>
    public string KindLabel => IsBoilerplate ? "Boilerplate · Not verified" : "Mechanical · Proven";

    /// <summary>In words, e.g. "pure rename `Foo`→`Bar`" or "3 registrations: 2 scoped, 1 singleton".</summary>
    public string Reason { get; } = hunk.Reasons.FirstOrDefault() ?? (hunk.Fold == FoldKind.Boilerplate ? "boilerplate" : "mechanical change");

    public string LinesLabel { get; } = Plural(hunk.ChangedLines, "line");

    public bool IsExpanded { get; } = expanded;

    public string ToggleLabel => IsExpanded ? "Close" : "Open";

    public bool HasGroup => members.Count > 1;

    /// <summary>"2 of 12": where this hunk sits in its group; the group's title is the tooltip.</summary>
    public string GroupLabel => HasGroup ? string.Create(CultureInfo.InvariantCulture, $"{index + 1} of {members.Count}") : "";

    public string GroupTitle => group?.Title ?? "";

    public bool HasPrevious => HasGroup && index > 0;

    public bool HasNext => HasGroup && index < members.Count - 1;

    [RelayCommand]
    private void Toggle() => owner.ToggleFold(Id);

    [RelayCommand]
    private Task Previous() => owner.GoToGroupMemberAsync(members, index - 1);

    [RelayCommand]
    private Task Next() => owner.GoToGroupMemberAsync(members, index + 1);

    public static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count:N0} {noun}{(count == 1 ? "" : "s")}");
}

public static class DiffFolds
{
    public static string IdFor(HunkTriage hunk) =>
        string.Create(CultureInfo.InvariantCulture, $"{hunk.Path}#{hunk.OldStart}:{hunk.NewStart}");

    /// <summary>
    /// Folds only what the engine marked as folded: proven mechanical, or a listed boilerplate shape. Proven-but-worth-
    /// seeing hunks (ripple call sites, moves into another type) carry <see cref="FoldKind.Open"/> and stay open.
    /// </summary>
    public static bool Folds(HunkTriage hunk) => hunk.Fold is FoldKind.Mechanical or FoldKind.Boilerplate;

    public static IReadOnlyList<DiffFold> For(TriageReady? triage, string path, IReadOnlySet<string> expanded) =>
        triage is null
            ? []
            : [.. triage.Hunks.Where(h => h.Path == path && Folds(h)).Select(h => new DiffFold(IdFor(h), h.OldStart, h.OldEnd, h.NewStart, h.NewEnd, expanded.Contains(IdFor(h))))];
}
