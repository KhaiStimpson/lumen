using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumen.App.Map;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

public enum MapLayoutKind
{
    Blueprint,
    Terrain,
}

public enum MapLens
{
    Attention,
    SinceReview,
    Tests,
}

/// <summary>A file tile on the Map.</summary>
public sealed class MapFileViewModel(FileEntryViewModel entry, string componentId)
{
    public FileEntryViewModel Entry { get; } = entry;

    public string Path => Entry.Path;

    public string Name => Entry.Name;

    public string Directory => Entry.Directory;

    public int Additions => Entry.Model.Additions;

    public int Deletions => Entry.Model.Deletions;

    public string ComponentId { get; } = componentId;

    /// <summary>Code (as opposed to markup, config or docs), which is what the Tests lens judges.</summary>
    public bool IsCode { get; } = IsCodePath(entry.Path);

    public IReadOnlyList<RiskViewModel> Risks { get; init; } = [];

    public int OpenThreads { get; init; }

    public bool IsSinceReview { get; init; }

    /// <summary>Test files that exercise this one, found in the code.</summary>
    public IReadOnlyList<string> TestedBy { get; init; } = [];

    public bool IsTested => TestedBy.Count > 0;

    private static readonly string[] CodeExtensions = [".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".java", ".kt", ".rs", ".sql", ".fs"];

    public static bool IsCodePath(string path) => CodeExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

/// <summary>A component box on the Map.</summary>
public sealed class MapComponentViewModel(string id, string title, string role, ComponentLayer layer, IReadOnlyList<MapFileViewModel> files)
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    public string Role { get; } = role;

    public ComponentLayer Layer { get; } = layer;

    public string LayerLabel => MapLayout.LayerLabel(Layer);

    public IReadOnlyList<MapFileViewModel> Files { get; } = files;

    public int Additions => Files.Sum(f => f.Additions);

    public MapNode Node { get; } = new(id, title, layer, [.. files.Select(f => new MapLeaf(f.Path, f.Additions))]);
}

/// <summary>Everything the Map is built from; <see cref="PullRequestViewModel"/> gathers it.</summary>
public sealed record MapInput(
    IReadOnlyList<MapComponentViewModel> Components,
    IReadOnlyList<MapLink> Links,
    bool FromWalkthrough,
    bool CanShowSince,
    int TestFiles,
    int TestLines,
    int GeneratedFiles,
    int GeneratedLines);

/// <summary>
/// The Map tab (plan v3): the pull request as the system it builds. Blueprint places the components by how they use
/// each other; Terrain sizes them by lines. Both draw the same boxes and files, so switching morphs one into the other.
/// Lenses colour the files by attention (risks, threads), what changed since your last review, or which have tests.
/// </summary>
public sealed partial class MapViewModel : ObservableObject
{
    private Dictionary<string, MapFileViewModel> _files = new(StringComparer.Ordinal);

    public IReadOnlyList<MapComponentViewModel> Components { get; private set; } = [];

    public IReadOnlyList<MapLink> Links { get; private set; } = [];

    /// <summary>
    /// The links the Blueprint draws: configuration and wiring (DI registration, settings pages) use nearly everything,
    /// so their outgoing links would only tangle the drawing. They still show beside the Map as "Uses".
    /// </summary>
    public IReadOnlyList<MapLink> DrawnLinks { get; private set; } = [];

    /// <summary>Bumped whenever the contents change, so the canvas re-lays out.</summary>
    [ObservableProperty]
    public partial int Version { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlueprint), nameof(IsTerrain), nameof(Title), nameof(Hint))]
    public partial MapLayoutKind Layout { get; set; }

    public bool IsBlueprint => Layout == MapLayoutKind.Blueprint;

    public bool IsTerrain => Layout == MapLayoutKind.Terrain;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LegendText))]
    public partial MapLens Lens { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFile), nameof(SelectedComponent), nameof(HasSelection), nameof(UsesLabel), nameof(UsedByLabel),
        nameof(TestedByLabel), nameof(SelectedRisks), nameof(HasSelectedRisks), nameof(ThreadsLabel), nameof(HasThreads))]
    public partial string? SelectedPath { get; set; }

    /// <summary>The file under the pointer, which the canvas outlines.</summary>
    [ObservableProperty]
    public partial string? HoverPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Hint))]
    public partial bool CanShowBlueprint { get; private set; }

    [ObservableProperty]
    public partial bool CanShowSince { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    public partial bool FromWalkthrough { get; private set; }

    [ObservableProperty]
    public partial string TestsLabel { get; private set; } = "";

    [ObservableProperty]
    public partial string GeneratedLabel { get; private set; } = "";

    public bool IsEmpty => Components.Count == 0;

    public string Title => IsBlueprint ? "How the change fits together" : "Where the lines are";

    public string Hint => !CanShowBlueprint
        ? "Components sized by lines added. Links between them appear once the walkthrough has read the code."
        : IsBlueprint
            ? (FromWalkthrough ? "Components the walkthrough found, placed by how they use each other" : "Change groups, placed by how they use each other")
              + " — callers on the left. Each box holds its files, sized by lines added."
            : "The same components and files, sized by lines added. B shows how they connect.";

    public string LegendText => Lens switch
    {
        MapLens.SinceReview => "Blue: changed since your last review",
        MapLens.Tests => "Blue: a test exercises it · amber: no test found · grey: markup and config",
        _ => "Amber: a risky area · blue badge: open threads · grey: markup and config",
    };

    public event EventHandler? OpenRequested;

    public MapFileViewModel? SelectedFile => SelectedPath is { } path ? _files.GetValueOrDefault(path) : null;

    public MapComponentViewModel? SelectedComponent => SelectedFile is { } file ? Components.FirstOrDefault(c => c.Id == file.ComponentId) : null;

    public bool HasSelection => SelectedFile is not null;

    public string UsesLabel => Neighbours(l => l.From, l => l.To);

    public string UsedByLabel => Neighbours(l => l.To, l => l.From);

    public string TestedByLabel => SelectedFile switch
    {
        null => "",
        { IsCode: false } => "Not code",
        { TestedBy.Count: 0 } => "No test found in this pull request",
        var f => string.Join(", ", f.TestedBy),
    };

    public IReadOnlyList<RiskViewModel> SelectedRisks => SelectedFile?.Risks ?? [];

    public bool HasSelectedRisks => SelectedRisks.Count > 0;

    public bool HasThreads => SelectedFile?.OpenThreads > 0;

    public string ThreadsLabel => SelectedFile?.OpenThreads switch
    {
        null or 0 => "",
        1 => "1 open thread",
        var n => string.Create(CultureInfo.InvariantCulture, $"{n} open threads"),
    };

    public MapFileViewModel? File(string path) => _files.GetValueOrDefault(path);

    public void Load(MapInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Components = input.Components;
        Links = input.Links;
        var configuration = input.Components.Where(c => c.Layer == ComponentLayer.Config).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        DrawnLinks = [.. input.Links.Where(l => !configuration.Contains(l.From))];
        _files = input.Components.SelectMany(c => c.Files).ToDictionary(f => f.Path, StringComparer.Ordinal);
        FromWalkthrough = input.FromWalkthrough;
        var hadBlueprint = CanShowBlueprint;
        CanShowBlueprint = DrawnLinks.Count > 0;
        CanShowSince = input.CanShowSince;
        if (!CanShowBlueprint)
        {
            Layout = MapLayoutKind.Terrain;
        }
        else if (!hadBlueprint)
        {
            Layout = MapLayoutKind.Blueprint;
        }

        if (!CanShowSince && Lens == MapLens.SinceReview)
        {
            Lens = MapLens.Attention;
        }

        TestsLabel = Count(input.TestFiles, input.TestLines, "test file");
        GeneratedLabel = Count(input.GeneratedFiles, input.GeneratedLines, "generated file");

        if (SelectedPath is null || !_files.ContainsKey(SelectedPath))
        {
            SelectedPath = _files.Values.OrderByDescending(f => f.Risks.Count).ThenByDescending(f => f.Additions).FirstOrDefault()?.Path;
        }

        OnPropertyChanged(nameof(IsEmpty));
        NotifySelection();
        Version++;
    }

    /// <summary>The selection's details depend on the contents too, so they refresh after a reload.</summary>
    private void NotifySelection()
    {
        foreach (var name in (string[])[nameof(SelectedFile), nameof(SelectedComponent), nameof(HasSelection), nameof(UsesLabel), nameof(UsedByLabel),
                     nameof(TestedByLabel), nameof(SelectedRisks), nameof(HasSelectedRisks), nameof(ThreadsLabel), nameof(HasThreads)])
        {
            OnPropertyChanged(name);
        }
    }

    [RelayCommand]
    public void ShowBlueprint()
    {
        if (CanShowBlueprint)
        {
            Layout = MapLayoutKind.Blueprint;
        }
    }

    [RelayCommand]
    public void ShowTerrain() => Layout = MapLayoutKind.Terrain;

    [RelayCommand]
    public void SetLens(MapLens lens)
    {
        if (lens != MapLens.SinceReview || CanShowSince)
        {
            Lens = lens;
        }
    }

    /// <summary>Attention → Since my review → Tests (skipping Since when there's no earlier review).</summary>
    public void NextLens() => Lens = Lens switch
    {
        MapLens.Attention when CanShowSince => MapLens.SinceReview,
        MapLens.Attention or MapLens.SinceReview => MapLens.Tests,
        _ => MapLens.Attention,
    };

    public void Select(string? path)
    {
        if (path is null || _files.ContainsKey(path))
        {
            SelectedPath = path;
        }
    }

    /// <summary>Selecting a component selects its largest file.</summary>
    public void SelectComponent(string id)
    {
        if (Components.FirstOrDefault(c => c.Id == id) is { Files.Count: > 0 } component)
        {
            SelectedPath = component.Files.OrderByDescending(f => f.Additions).First().Path;
        }
    }

    [RelayCommand]
    public void Open()
    {
        if (HasSelection)
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private string Neighbours(Func<MapLink, string> self, Func<MapLink, string> other)
    {
        if (SelectedComponent is not { } component)
        {
            return "";
        }

        // "Contact data model (proposes fields)"; a link only the model suggested says so.
        var titles = Links.Where(l => self(l) == component.Id)
            .Select(l => (Title: Components.FirstOrDefault(c => c.Id == other(l))?.Title, Link: l))
            .Where(x => x.Title is not null)
            .Select(x => x.Link.Label.Length > 0 || x.Link.Inferred
                ? $"{x.Title} ({string.Join(", ", new[] { x.Link.Label, x.Link.Inferred ? "inferred" : "" }.Where(p => p.Length > 0))})"
                : x.Title!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return titles.Count == 0 ? "—" : string.Join(", ", titles);
    }

    private static string Count(int files, int lines, string noun) => files == 0
        ? ""
        : string.Create(CultureInfo.InvariantCulture, $"{files} {noun}{(files == 1 ? "" : "s")} · +{lines:N0}");
}
