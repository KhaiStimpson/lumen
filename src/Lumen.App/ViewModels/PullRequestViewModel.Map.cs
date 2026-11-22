using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Lumen.App.Map;
using Lumen.Contracts;
using ComponentLayer = Lumen.Contracts.ComponentLayer;
using LinkKind = Lumen.Contracts.LinkKind;
using TestPaths = Lumen.Domain.TestPaths;

namespace Lumen.App.ViewModels;

/// <summary>
/// The Map tab (plan v3): the walkthrough's components and the code's links, drawn as a blueprint or a terrain. It
/// never waits on the model: without a walkthrough the Map still shows the change groups or folders, sized by lines.
/// </summary>
public sealed partial class PullRequestViewModel
{
    private const int MaxFolderComponents = 12;

    /// <summary>Paths changed after the viewer's last review, fetched the first time the Since lens is used.</summary>
    private HashSet<string>? _mapSincePaths;

    private bool _loadingMapSince;

    public MapViewModel Map { get; } = new();

    partial void OnTabChanged(PullRequestTab value)
    {
        if (value == PullRequestTab.Map)
        {
            RebuildMap();
        }
    }

    /// <summary>M: the Map, or back to the files from it.</summary>
    [RelayCommand]
    public void ToggleMap() => ShowTab(IsMapTab ? PullRequestTab.Files : PullRequestTab.Map);

    /// <summary>Opens the Map's selected file in the diff.</summary>
    [RelayCommand]
    public async Task OpenMapSelectionAsync()
    {
        if (Map.SelectedFile is not { } file)
        {
            return;
        }

        ShowTab(PullRequestTab.Files);
        await SelectFileAsync(file.Entry).ConfigureAwait(true);
    }

    /// <summary>A risk listed beside the Map: shown in the diff.</summary>
    [RelayCommand]
    public async Task OpenMapRiskAsync(RiskViewModel? risk)
    {
        if (risk is null)
        {
            return;
        }

        ShowTab(PullRequestTab.Files);
        await SelectRiskAsync(risk).ConfigureAwait(true);
    }

    internal void RebuildMap()
    {
        var walkthrough = Walkthrough?.Model;
        var drawable = Files.Where(IsMapFile).ToList();
        var links = walkthrough?.FileLinks ?? [];

        // Components: the walkthrough's, else its change groups, else folders.
        List<(string Id, string Title, string Role, ComponentLayer Layer, List<string> Paths)> raw;
        var fromWalkthrough = walkthrough is { Components.Count: > 0 };
        if (fromWalkthrough)
        {
            raw = [.. walkthrough!.Components.Select(c => (c.Id, c.Title, c.Role, c.Layer, c.Paths.ToList()))];
        }
        else if (walkthrough is { Groups.Count: > 0 })
        {
            raw = [.. walkthrough.Groups.Where(g => !g.IsMechanical).Select(g => (g.Id, g.Title, g.Summary, ComponentLayer.Other, g.Paths.ToList()))];
        }
        else
        {
            raw = FolderComponents(drawable);
        }

        var byPath = drawable.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var risks = Walkthrough?.Risks.GroupBy(r => r.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlyList<RiskViewModel>)[.. g], StringComparer.Ordinal)
                    ?? [];
        var testedBy = links.Where(l => l.Kind == LinkKind.Tests)
            .GroupBy(l => l.To, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(l => System.IO.Path.GetFileNameWithoutExtension(l.From)).Distinct(StringComparer.Ordinal)], StringComparer.Ordinal);
        var since = _mapSincePaths;

        MapFileViewModel Tile(FileEntryViewModel entry, string componentId) => new(entry, componentId)
        {
            Risks = risks.GetValueOrDefault(entry.Path) ?? [],
            OpenThreads = Threads.Count(t => t.IsOpen && t.Path == entry.Path),
            IsSinceReview = since?.Contains(entry.Path) == true,
            TestedBy = testedBy.GetValueOrDefault(entry.Path) ?? [],
        };

        var components = new List<MapComponentViewModel>();
        foreach (var (id, title, role, layer, paths) in raw)
        {
            var files = paths.Where(p => byPath.ContainsKey(p) && placed.Add(p)).Select(p => Tile(byPath[p], id)).ToList();
            if (files.Count > 0)
            {
                components.Add(new MapComponentViewModel(id, title, role, layer, files));
            }
        }

        var rest = drawable.Where(f => !placed.Contains(f.Path)).ToList();
        if (rest.Count > 0)
        {
            components.Add(new MapComponentViewModel("other", components.Count == 0 ? "Changes" : "Other changes", "", ComponentLayer.Other,
                [.. rest.Select(f => Tile(f, "other"))]));
        }

        // Links: the walkthrough's component edges, else the code's file links rolled up to whatever the components are.
        List<MapLink> mapLinks;
        if (fromWalkthrough)
        {
            var ids = components.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            mapLinks = [.. walkthrough!.Edges.Where(e => ids.Contains(e.From) && ids.Contains(e.To)).Select(e => new MapLink(e.From, e.To, e.Inferred, e.Label, e.Kind))];
        }
        else
        {
            var componentOf = components.SelectMany(c => c.Files.Select(f => (f.Path, c.Id))).ToDictionary(x => x.Path, x => x.Id, StringComparer.Ordinal);
            mapLinks = [.. links.Where(l => l.Kind != LinkKind.Tests)
                .Select(l => (From: componentOf.GetValueOrDefault(l.From), To: componentOf.GetValueOrDefault(l.To), l.Kind))
                .Where(l => l.From is not null && l.To is not null && l.From != l.To)
                .GroupBy(l => (l.From, l.To))
                .Select(g => new MapLink(g.Key.From!, g.Key.To!, false, "", g.Max(l => l.Kind)))];
        }

        var tests = Files.Where(f => !f.IsMechanical && TestPaths.IsTest(f.Path)).ToList();
        var generated = Files.Where(f => f.IsMechanical).ToList();
        Map.Load(new MapInput(
            components,
            mapLinks,
            fromWalkthrough,
            CanShowSinceLastReview,
            tests.Count,
            tests.Sum(f => f.Model.Additions),
            generated.Count,
            generated.Sum(f => f.Model.Additions)));

        // A new head clears what changed since the last review; the lens fetches it again.
        if (since is null && Map.Lens == MapLens.SinceReview)
        {
            _ = LoadMapSinceAsync();
        }
    }

    private static bool IsMapFile(FileEntryViewModel file) =>
        !file.IsMechanical && !file.Model.IsBinary && file.Model.Kind != FileChangeKind.Deleted && !TestPaths.IsTest(file.Path);

    /// <summary>Without a walkthrough: one component per folder, the smallest folded into "Other changes".</summary>
    private static List<(string Id, string Title, string Role, ComponentLayer Layer, List<string> Paths)> FolderComponents(List<FileEntryViewModel> files)
    {
        var folders = files.GroupBy(f => f.Directory.Length == 0 ? "(root)" : f.Directory, StringComparer.Ordinal)
            .OrderByDescending(g => g.Sum(f => f.Model.Additions))
            .ToList();
        return
        [
            .. folders.Take(MaxFolderComponents).Select((g, i) => (
                $"folder{i + 1}",
                g.Key.Contains('/', StringComparison.Ordinal) ? g.Key[(g.Key.LastIndexOf('/') + 1)..] : g.Key,
                g.Key,
                ComponentLayer.Other,
                g.Select(f => f.Path).ToList())),
        ];
    }

    private void OnMapPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapViewModel.Lens) && Map.Lens == MapLens.SinceReview && _mapSincePaths is null)
        {
            _ = LoadMapSinceAsync();
        }
    }

    private async Task LoadMapSinceAsync()
    {
        if (_loadingMapSince || Details?.LastReviewedSha is not { Length: > 0 } sha || !CanShowSinceLastReview)
        {
            return;
        }

        _loadingMapSince = true;
        try
        {
            var range = await _source.GetDiffRangeAsync(
                new GetDiffRangeRequest { PullRequest = Ref, Range = new CommitRange { FromSha = sha, ToSha = _headSha } },
                _lifetime.Token).ConfigureAwait(true);
            _mapSincePaths = range.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            RebuildMap();
        }
        catch (RpcException ex)
        {
            Toast = $"Couldn't find what changed since your last review: {ex.Status.Detail}";
            Map.SetLens(MapLens.Attention);
        }
        finally
        {
            _loadingMapSince = false;
        }
    }
}
