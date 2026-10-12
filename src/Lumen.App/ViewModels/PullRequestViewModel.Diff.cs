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
}
