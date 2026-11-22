using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumen.App.Diff;

namespace Lumen.App.ViewModels;

/// <summary>Diff upgrades (plan v2 M6): split view, whitespace-insensitive diffs and find in file.</summary>
public sealed partial class PullRequestViewModel
{
    /// <summary>Side-by-side instead of unified. The shell persists it per user.</summary>
    [ObservableProperty]
    public partial bool IsSplitView { get; set; }

    /// <summary><c>git diff -w</c>: hide changes that only touch whitespace.</summary>
    [ObservableProperty]
    public partial bool IgnoreWhitespace { get; set; }

    partial void OnIgnoreWhitespaceChanged(bool value) => _ = ApplyIgnoreWhitespaceAsync();

    private async Task ApplyIgnoreWhitespaceAsync()
    {
        if (Scope is { } scope)
        {
            await ApplyScopeAsync(scope).ConfigureAwait(true);
        }
        else if (SelectedFile is { } file)
        {
            await SelectFileAsync(file).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public void ToggleSplitView() => IsSplitView = !IsSplitView;

    [RelayCommand]
    public void ToggleIgnoreWhitespace() => IgnoreWhitespace = !IgnoreWhitespace;

    /// <summary>A file moved without changes has no diff; this shows its content.</summary>
    [RelayCommand]
    public void ShowWholeFile() => ShowFullFile = true;

    // Find in file (Ctrl+F) ------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsFindOpen { get; set; }

    [ObservableProperty]
    public partial string FindQuery { get; set; } = "";

    /// <summary>Document lines (1-based), column and length of each match in the shown file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindStatus))]
    public partial IReadOnlyList<FindMatch> FindMatches { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindStatus), nameof(CurrentFindMatch))]
    public partial int FindIndex { get; private set; } = -1;

    public FindMatch? CurrentFindMatch => FindIndex >= 0 && FindIndex < FindMatches.Count ? FindMatches[FindIndex] : null;

    public string FindStatus => FindQuery.Length == 0 ? "" : FindMatches.Count == 0 ? "No matches"
        : string.Create(CultureInfo.InvariantCulture, $"{FindIndex + 1} of {FindMatches.Count}");

    /// <summary>Asks the view to show the current match.</summary>
    public event EventHandler<FindMatch>? FindMatchRequested;

    [RelayCommand]
    public void OpenFind() => IsFindOpen = true;

    [RelayCommand]
    public void CloseFind()
    {
        IsFindOpen = false;
        FindQuery = "";
    }

    partial void OnFindQueryChanged(string value) => RunFind(keepPosition: false);

    partial void OnCurrentDiffChanged(Diff.DiffDocument? value)
    {
        OnPropertyChanged(nameof(ChangePositionLabel));
        OnPropertyChanged(nameof(HasChanges));
        if (FindQuery.Length > 0)
        {
            RunFind(keepPosition: true);
        }
    }

    [RelayCommand]
    public void FindNext() => StepFind(+1);

    [RelayCommand]
    public void FindPrevious() => StepFind(-1);

    private void StepFind(int direction)
    {
        if (FindMatches.Count == 0)
        {
            return;
        }

        FindIndex = (FindIndex + direction + FindMatches.Count) % FindMatches.Count;
        FindMatchRequested?.Invoke(this, FindMatches[FindIndex]);
    }

    /// <summary>Searches the shown text (diff or full file), case-insensitively, skipping card placeholders.</summary>
    private void RunFind(bool keepPosition)
    {
        var query = FindQuery;
        if (query.Length == 0 || CurrentDiff is not { } diff)
        {
            FindMatches = [];
            FindIndex = -1;
            return;
        }

        var matches = new List<FindMatch>();
        var lines = diff.Text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (diff.RowAt(i + 1)?.Kind == Diff.DiffRowKind.Card)
            {
                continue;
            }

            for (var at = lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0; at = lines[i].IndexOf(query, at + query.Length, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new FindMatch(i + 1, at, query.Length));
            }
        }

        var previous = CurrentFindMatch;
        FindMatches = matches;
        FindIndex = matches.Count == 0 ? -1
            : keepPosition && previous is not null ? Math.Max(0, matches.FindIndex(m => m.DocumentLine >= previous.DocumentLine))
            : 0;
        if (CurrentFindMatch is { } current)
        {
            FindMatchRequested?.Invoke(this, current);
        }
    }

    // Change navigation ([ and ], the overview ruler) ---------------------------------------------

    private int _currentChange = -1;

    /// <summary>The view asks its editor to step; +1 for the next change, -1 for the previous one.</summary>
    public event EventHandler<int>? ChangeStepRequested;

    public bool HasChanges => CurrentDiff is { Changes.Count: > 0 };

    /// <summary>"Change 3 of 7" once stepping, else how many there are.</summary>
    public string ChangePositionLabel => CurrentDiff is not { Changes.Count: > 0 and var count } ? ""
        : _currentChange >= 0 && _currentChange < count ? string.Create(CultureInfo.InvariantCulture, $"Change {_currentChange + 1} of {count}")
        : count == 1 ? "1 change" : string.Create(CultureInfo.InvariantCulture, $"{count} changes");

    /// <summary>The editor moved to another change (by key, button or ruler).</summary>
    public void SetCurrentChange(int index)
    {
        _currentChange = index;
        OnPropertyChanged(nameof(ChangePositionLabel));
    }

    [RelayCommand]
    public void NextChange() => ChangeStepRequested?.Invoke(this, +1);

    [RelayCommand]
    public void PreviousChange() => ChangeStepRequested?.Invoke(this, -1);

    /// <summary>
    /// Opens the next (+1) or previous (-1) file in the files tree's order, skipping files with nothing to step through.
    /// False at either end.
    /// </summary>
    public async Task<bool> StepFileAsync(int direction)
    {
        var ordered = Tree.SelectMany(n => n.Descendants().Prepend(n)).Select(n => n.File).OfType<FileEntryViewModel>().ToList();
        var at = SelectedFile is null ? -1 : ordered.FindIndex(f => f.Path == SelectedFile.Path);
        for (var i = at + direction; i >= 0 && i < ordered.Count; i += direction)
        {
            if (ordered[i].Model.Additions + ordered[i].Model.Deletions > 0)
            {
                await SelectFileAsync(ordered[i]).ConfigureAwait(true);
                return true;
            }
        }

        return false;
    }
}
