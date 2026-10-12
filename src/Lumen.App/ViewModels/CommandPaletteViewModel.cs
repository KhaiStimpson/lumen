using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lumen.App.ViewModels;

public enum PaletteItemKind
{
    File,
    PullRequest,
    Action,
}

public sealed partial class PaletteItemViewModel(PaletteItemKind kind, string title, string detail, string shortcut, Func<Task> run) : ObservableObject
{
    public PaletteItemKind Kind { get; } = kind;

    public string Title { get; } = title;

    public string Detail { get; } = detail;

    /// <summary>The key that does the same thing without the palette (TDD §30), e.g. "J" or "Ctrl+F".</summary>
    public string Shortcut { get; } = shortcut;

    public bool HasShortcut => Shortcut.Length > 0;

    public string KindLabel => Kind switch
    {
        PaletteItemKind.File => "File",
        PaletteItemKind.PullRequest => "Pull request",
        _ => "Action",
    };

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public Task RunAsync() => run();
}

/// <summary>
/// Ctrl+K (TDD §30, plan v2 M6): go to file, go to pull request, and every action with its shortcut. Type to filter;
/// "&gt;" lists actions only and "#" pull requests only.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private const int MaxItems = 40;
    private readonly Func<IEnumerable<PaletteItemViewModel>> _sources;

    public CommandPaletteViewModel(Func<IEnumerable<PaletteItemViewModel>> sources)
    {
        _sources = sources;
    }

    public ObservableCollection<PaletteItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial PaletteItemViewModel? Selected { get; private set; }

    public bool HasItems => Items.Count > 0;

    public string Hint => Query.StartsWith('>') ? "Actions" : Query.StartsWith('#') ? "Pull requests" : "Files, pull requests and actions · > actions · # pull requests";

    partial void OnQueryChanged(string value)
    {
        IsOpen = true;
        Refresh();
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            Refresh();
        }
    }

    public void Open()
    {
        IsOpen = true;
        Refresh();
    }

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        Query = "";
        IsOpen = false;
    }

    public void Refresh()
    {
        var query = Query.Trim();
        var only = query.StartsWith('>') ? PaletteItemKind.Action : query.StartsWith('#') ? PaletteItemKind.PullRequest : (PaletteItemKind?)null;
        var text = only is null ? query : query[1..].Trim();
        var matches = _sources()
            .Where(i => only is null || i.Kind == only)
            .Select(i => (Item: i, Score: Score(i, text)))
            .Where(m => m.Score > 0)
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Item.Kind)
            .Take(MaxItems)
            .Select(m => m.Item)
            .ToList();

        Items.Clear();
        foreach (var item in matches)
        {
            Items.Add(item);
        }

        Select(Items.FirstOrDefault());
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Hint));
    }

    /// <summary>Empty text keeps everything (actions first); otherwise prefix, word start, then subsequence.</summary>
    internal static int Score(PaletteItemViewModel item, string text)
    {
        if (text.Length == 0)
        {
            return item.Kind == PaletteItemKind.Action ? 3 : item.Kind == PaletteItemKind.PullRequest ? 2 : 1;
        }

        var title = item.Title;
        if (title.StartsWith(text, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        var at = title.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            return at > 0 && !char.IsLetterOrDigit(title[at - 1]) ? 80 : 60;
        }

        if (item.Detail.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return 40;
        }

        // Subsequence ("abrprov" → "AbrAbnProvider.cs").
        var position = 0;
        foreach (var c in title)
        {
            if (position < text.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(text[position]))
            {
                position++;
            }
        }

        return position == text.Length ? 20 : 0;
    }

    public void Move(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }

        var index = Selected is null ? -1 : Items.IndexOf(Selected);
        Select(Items[(index + delta + Items.Count) % Items.Count]);
    }

    private void Select(PaletteItemViewModel? item)
    {
        if (Selected is { } previous)
        {
            previous.IsSelected = false;
        }

        Selected = item;
        if (item is not null)
        {
            item.IsSelected = true;
        }
    }

    [RelayCommand]
    public async Task RunAsync(PaletteItemViewModel? item)
    {
        item ??= Selected;
        if (item is null)
        {
            return;
        }

        Close();
        await item.RunAsync().ConfigureAwait(true);
    }
}
