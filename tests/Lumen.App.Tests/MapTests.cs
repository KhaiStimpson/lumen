using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Lumen.App.Map;
using Lumen.App.ViewModels;
using Lumen.Contracts;

namespace Lumen.App.Tests;

/// <summary>The Map (plan v3): the change as the system it builds, as a blueprint or a terrain.</summary>
public sealed class MapTests
{
    private const string Enrichment = "src/AndrewCrm.Web/Services/Enrichment/";

    // ---------------------------------------------------------------- Layout

    private static MapNode Node(string id, ComponentLayer layer, params int[] lines) =>
        new(id, id, layer, [.. lines.Select((l, i) => new MapLeaf($"{id}/{i}.cs", l))]);

    [Fact]
    public void SquarifyFillsTheAreaInProportionToTheWeights()
    {
        var area = new Rect(10, 20, 400, 300);
        var tiles = MapLayout.Squarify([("a", 6), ("b", 3), ("c", 2), ("d", 1)], area);

        Assert.Equal(area.Width * area.Height, tiles.Values.Sum(r => r.Width * r.Height), 3);
        Assert.Equal(0.5, tiles["a"].Width * tiles["a"].Height / (area.Width * area.Height), 3);
        Assert.All(tiles.Values, r => Assert.True(area.Contains(r.Center)));

        // Near square, not slivers.
        Assert.All(tiles.Values, r => Assert.InRange(Math.Max(r.Width / r.Height, r.Height / r.Width), 1, 3));
    }

    [Fact]
    public void TerrainSizesComponentsByLinesAndKeepsEveryFileInsideItsBox()
    {
        var nodes = new[] { Node("big", ComponentLayer.Core, 300, 100), Node("small", ComponentLayer.Data, 20) };

        var map = MapLayout.Terrain(nodes, new Size(800, 500));

        var big = map.Components["big"];
        var small = map.Components["small"];
        Assert.True(big.Width * big.Height > 10 * small.Width * small.Height);
        Assert.True(big.Contains(map.Files["big/0.cs"]) && big.Contains(map.Files["big/1.cs"]));
        Assert.True(small.Contains(map.Files["small/0.cs"]));

        // A one-line file still gets a clickable tile.
        Assert.True(MapLayout.Terrain([Node("n", ComponentLayer.Core, 400, 1)], new Size(800, 500)).Files["n/1.cs"].Width > 4);
    }

    [Fact]
    public void BlueprintPutsCallersLeftAndImplementationsAfterTheirInterface()
    {
        var nodes = new[]
        {
            Node("provider", ComponentLayer.Integration, 120),
            Node("data", ComponentLayer.Data, 80),
            Node("seam", ComponentLayer.Core, 40),
            Node("job", ComponentLayer.Entry, 60),
            Node("service", ComponentLayer.Core, 100),
            Node("notes", ComponentLayer.Other, 30),
        };
        var links = new[]
        {
            new MapLink("job", "service", false, ""),
            new MapLink("service", "seam", false, "", LinkKind.DependsOn),
            new MapLink("provider", "seam", false, "", LinkKind.Implements),
            new MapLink("provider", "data", false, ""),
        };

        var map = MapLayout.Blueprint(nodes, links, new Size(1000, 600));

        double X(string id) => map.Components[id].X;
        Assert.True(X("job") < X("service"));
        Assert.True(X("service") < X("seam"));
        Assert.True(X("seam") < X("provider"));
        Assert.True(X("provider") < X("data"));

        // Implementations are drawn from their interface; every connector runs left to right into its target.
        Assert.Contains(map.Routes, r => (r.From, r.To, r.Label) == ("seam", "provider", "implemented by"));
        Assert.All(map.Routes, r => Assert.True(r.Points[^1].X > r.Points[0].X));
        Assert.Equal(["Entry points", "Core", "Core", "Integrations", "Data"], map.Columns.Select(c => c.Label));

        // Boxes stay inside the canvas and don't overlap in a column.
        Assert.All(map.Components.Values, r => Assert.True(new Rect(0, 0, 1000, 600).Contains(r)));
        Assert.All(map.Components.Values, a => Assert.DoesNotContain(map.Components.Values, b => a != b && a.Intersects(b)));
    }

    [Fact]
    public void BlueprintSpreadsTheConnectorsOfOneBoxAlongItsSide()
    {
        var nodes = new[] { Node("hub", ComponentLayer.Core, 200), Node("a", ComponentLayer.Data, 50), Node("b", ComponentLayer.Data, 50), Node("c", ComponentLayer.Data, 50) };
        var links = new[] { new MapLink("hub", "a", false, ""), new MapLink("hub", "b", false, ""), new MapLink("hub", "c", false, "") };

        var map = MapLayout.Blueprint(nodes, links, new Size(800, 500));

        var starts = map.Routes.Select(r => r.Points[0].Y).ToList();
        Assert.Equal(3, starts.Distinct().Count());
        Assert.Equal(starts.Order(), map.Routes.OrderBy(r => map.Components[r.To].Y).Select(r => r.Points[0].Y));
    }

    [Fact]
    public void TilesNameFilesWithoutTheirExtensionUnlessThatWouldMislead()
    {
        Assert.Equal("EnrichmentService", MapCanvas.TileName("EnrichmentService.cs"));
        Assert.Equal("Ai.cshtml.cs", MapCanvas.TileName("Ai.cshtml.cs"));
        Assert.Equal("_ContactSidebar", MapCanvas.TileName("_ContactSidebar.cshtml"));
    }

    [Fact]
    public void AMorphMovesEveryBoxPartWay()
    {
        var from = new MapGeometry(new Dictionary<string, Rect> { ["a"] = new(0, 0, 100, 100) }, new Dictionary<string, Rect>(), [], []);
        var to = new MapGeometry(new Dictionary<string, Rect> { ["a"] = new(100, 50, 200, 100), ["b"] = new(1, 1, 1, 1) }, new Dictionary<string, Rect>(), [], []);

        var half = MapGeometry.Lerp(from, to, 0.5);

        Assert.Equal(new Rect(50, 25, 150, 100), half.Components["a"]);
        Assert.Equal(new Rect(1, 1, 1, 1), half.Components["b"]);
    }

    // ---------------------------------------------------------------- The Map tab

    [AvaloniaFact]
    public async Task MOpensABlueprintOfTheWalkthroughsComponents()
    {
        await using var session = await AppSession.OpenAsync(viewer: "andrew-reviewer");
        var pr = session.PullRequest;

        await session.PressAsync(PhysicalKey.M);
        Assert.True(pr.IsMapTab);
        var map = pr.Map;
        Assert.True(map.FromWalkthrough);
        Assert.True(map.IsBlueprint);
        Assert.Equal(15, map.Components.Count);
        Assert.Contains(map.Components, c => c.Title == "Ingestion pipeline" && c.Layer == ComponentLayer.Core);

        // Tests and generated files stay off the map, counted below it.
        Assert.DoesNotContain(map.Components.SelectMany(c => c.Files), f => f.Path.StartsWith("tests/", StringComparison.Ordinal) || f.Path.Contains("/Migrations/", StringComparison.Ordinal));
        Assert.Equal("10 test files · +1,395", map.TestsLabel);
        Assert.StartsWith("14 generated files", map.GeneratedLabel, StringComparison.Ordinal);

        // It opens on the file with the most risks, and says how its component connects.
        Assert.Equal(Enrichment + "EnrichmentIngestionService.cs", map.SelectedPath);
        Assert.Equal(2, map.SelectedRisks.Count);
        Assert.Contains("Contact data model (proposes fields)", map.UsesLabel, StringComparison.Ordinal);
        Assert.Contains("Signals and timeline (writes activities, inferred)", map.UsesLabel, StringComparison.Ordinal);
        Assert.Contains("Enrichment service (hands findings)", map.UsedByLabel, StringComparison.Ordinal);
        Assert.Contains("EnrichmentIngestionServiceTests", map.TestedByLabel, StringComparison.Ordinal);

        // Laid out by how the components use each other: entry points first, data last.
        await Fixture.PumpAsync(40);
        var canvas = session.Find<MapCanvas>("MapCanvas");
        double X(string title) => canvas.Current.Components[map.Components.Single(c => c.Title == title).Id].X;
        Assert.True(X("Nightly scheduler") < X("Enrichment service"));
        Assert.True(X("Enrichment service") < X("Ingestion pipeline"));
        Assert.True(X("Ingestion pipeline") < X("Provider seam"));
        Assert.True(X("Provider seam") < X("Business registries"));
        Assert.True(X("Business registries") < X("Contact data model"));
        Assert.NotEmpty(canvas.Current.Routes);
        session.Capture("40-map-blueprint.png");

        await session.PressAsync(PhysicalKey.M);
        Assert.True(pr.IsFilesTab);
    }

    [AvaloniaFact]
    public async Task TerrainAndBlueprintMorphTheSameBoxes()
    {
        await using var session = await AppSession.OpenAsync(viewer: "andrew-reviewer");
        var pr = session.PullRequest;
        await session.PressAsync(PhysicalKey.M);
        await Fixture.PumpAsync(40);
        var canvas = session.Find<MapCanvas>("MapCanvas");
        var blueprint = canvas.Current;

        await session.PressAsync(PhysicalKey.T);
        Assert.True(pr.Map.IsTerrain);
        await Fixture.WaitUntilAsync(() => !canvas.IsMorphing);
        await Fixture.PumpAsync(5);
        var terrain = canvas.Current;

        // The same boxes and files, somewhere else; no connectors in Terrain.
        Assert.Equal(blueprint.Components.Keys.Order(), terrain.Components.Keys.Order());
        Assert.Equal(blueprint.Files.Keys.Order(), terrain.Files.Keys.Order());
        Assert.NotEqual(blueprint.Components["c10"], terrain.Components["c10"]);
        Assert.Empty(terrain.Routes);

        // Sized by lines: the 305-line ingestion service outweighs the 55-line DI registration.
        var area = (Rect r) => r.Width * r.Height;
        Assert.True(area(terrain.Components["c10"]) > 3 * area(terrain.Components["c14"]));
        session.Capture("41-map-terrain.png");

        await session.PressAsync(PhysicalKey.B);
        Assert.True(pr.Map.IsBlueprint);
        await Fixture.WaitUntilAsync(() => !canvas.IsMorphing);
        Assert.Equal(blueprint.Components["c10"], canvas.Current.Components["c10"]);
    }

    [AvaloniaFact]
    public async Task LensesColourByWhatChangedSinceYourReviewAndByTests()
    {
        await using var session = await AppSession.OpenAsync(viewer: "andrew-reviewer");
        var pr = session.PullRequest;
        pr.ShowTab(PullRequestTab.Map);
        var map = pr.Map;
        Assert.True(map.CanShowSince);

        // L cycles: Attention → Since my review (fetched once, from the last reviewed commit) → Tests.
        await session.PressAsync(PhysicalKey.L);
        Assert.Equal(MapLens.SinceReview, map.Lens);
        await Fixture.WaitUntilAsync(() => map.File("src/AndrewCrm.Web/Program.cs")?.IsSinceReview == true);
        Assert.False(map.File("src/AndrewCrm.Web/Data/Entities.cs")!.IsSinceReview);
        await Fixture.PumpAsync(20);
        session.Capture("42-map-since-review.png");

        await session.PressAsync(PhysicalKey.L);
        Assert.Equal(MapLens.Tests, map.Lens);
        Assert.True(map.File(Enrichment + "AbrAbnProvider.cs")!.IsTested);
        Assert.Equal(["ImportEnrichmentEnqueueTests"], map.File(Enrichment + "EnrichmentJobs.cs")!.TestedBy);
        Assert.False(map.File("src/AndrewCrm.Web/Services/CatchMeUpService.cs")!.IsTested);
        Assert.Empty(map.File("src/AndrewCrm.Web/Program.cs")!.TestedBy);
        map.Select("src/AndrewCrm.Web/Pages/Settings/Ai.cshtml");
        Assert.Equal("Not code", map.TestedByLabel);
        map.Select("src/AndrewCrm.Web/Program.cs");
        Assert.Equal("No test found in this pull request", map.TestedByLabel);
        Assert.Equal("1 open thread", map.ThreadsLabel);
        await Fixture.PumpAsync(20);
        session.Capture("43-map-tests.png");

        await session.PressAsync(PhysicalKey.L);
        Assert.Equal(MapLens.Attention, map.Lens);
    }

    [AvaloniaFact]
    public async Task EnterOpensTheSelectedFileInTheDiff()
    {
        await using var session = await AppSession.OpenAsync(viewer: "andrew-reviewer");
        var pr = session.PullRequest;
        await session.PressAsync(PhysicalKey.M);
        pr.Map.Select(Enrichment + "PeopleDataLabsProvider.cs");

        await session.PressAsync(PhysicalKey.Enter);
        await Fixture.WaitUntilAsync(() => pr.SelectedFile?.Path == Enrichment + "PeopleDataLabsProvider.cs");
        Assert.True(pr.IsFilesTab);

        // A risk beside the Map goes straight to its line.
        await session.PressAsync(PhysicalKey.M);
        await pr.OpenMapRiskAsync(pr.Map.SelectedRisks.Single());
        Assert.True(pr.IsFilesTab);
        Assert.Equal("PeopleDataLabsProvider.cs:40", pr.CurrentRisk!.LocationLabel);

        // Escape leaves the Map too.
        await session.PressAsync(PhysicalKey.M);
        await session.PressAsync(PhysicalKey.Escape);
        Assert.True(pr.IsFilesTab);
    }

    [AvaloniaFact]
    public async Task WithoutAWalkthroughTheMapStillShowsWhereTheLinesAre()
    {
        var source = Fixture.CreateSource();
        await source.UpdateConnectionSettingsAsync(new ConnectionSettings { AllowCloudReasoning = true, AllowCodeToFastModel = false }, CancellationToken.None);
        var pr = await Fixture.LoadAsync(source);
        await Fixture.WaitUntilAsync(() => pr.Walkthrough is not null);

        pr.ShowTab(PullRequestTab.Map);

        var map = pr.Map;
        Assert.False(map.FromWalkthrough);
        Assert.False(map.CanShowBlueprint);
        Assert.True(map.IsTerrain);
        Assert.Contains(map.Components, c => c.Title == "Enrichment" && c.Files.Count == 12);
        map.ShowBlueprint();
        Assert.True(map.IsTerrain);
        Assert.Contains("once the walkthrough has read the code", map.Hint, StringComparison.Ordinal);
        await pr.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task TheMapFollowsTheDarkTheme()
    {
        var app = Application.Current!;
        try
        {
            await using var session = await AppSession.OpenAsync(viewer: "andrew-reviewer");
            session.PullRequest.ShowTab(PullRequestTab.Map);
            session.PullRequest.Map.Select(Enrichment + "EnrichmentScheduler.cs");
            app.RequestedThemeVariant = ThemeVariant.Dark;
            await Fixture.PumpAsync(40);
            session.Capture("44-map-blueprint-dark.png");
        }
        finally
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
        }
    }
}
