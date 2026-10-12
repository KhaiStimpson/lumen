using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Lumen.App.Diff;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Lumen.App.Views;
using Lumen.Contracts;

namespace Lumen.App.Tests;

/// <summary>Diff upgrades (plan v2 M6), the fast model's connection (M7) and the walkthrough (M8).</summary>
public sealed class DiffUpgradeAndWalkthroughTests
{
    // ---------------------------------------------------------------- M6

    [AvaloniaFact]
    public async Task SplitViewPairsTheSidesAndIsRemembered()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;
        await pr.SelectFileAsync(pr.Files.Single(f => f.Path.EndsWith("CatchMeUpService.cs", StringComparison.Ordinal)));

        await session.PressAsync(PhysicalKey.S);
        await Fixture.PumpAsync(10);

        Assert.True(pr.IsSplitView);
        var split = session.Find<SplitDiffView>("SplitEditor");
        Assert.True(split.IsEffectivelyVisible);
        Assert.False(session.Find<DiffEditor>("Editor").IsVisible);
        var (left, right) = (split.Left.Diff!, split.Right.Diff!);
        Assert.Equal(left.Rows.Count, right.Rows.Count);
        Assert.DoesNotContain(left.Rows, r => r.Kind == DiffRowKind.Added);
        Assert.DoesNotContain(right.Rows, r => r.Kind == DiffRowKind.Removed);
        Assert.Contains(left.Rows, r => r.Kind == DiffRowKind.Filler);

        // Removed line 321 sits beside its replacement.
        var removedAt = left.DocumentLineForOldLine(321)!.Value;
        Assert.Equal(DiffRowKind.Added, right.RowAt(removedAt)!.Kind);

        // The outdated thread's card is on the head side with a spacer opposite.
        var thread = pr.Threads.Single(t => t.IsOutdated);
        Assert.NotNull(right.CardLine(thread.CardId));
        Assert.Equal(right.CardLine(thread.CardId), left.CardLine("spacer:" + thread.CardId));
        session.Capture("28-split.png");

        Assert.True(AppSettings.Load(session.SettingsPath).SplitView);
        await session.PressAsync(PhysicalKey.S);
        Assert.False(pr.IsSplitView);
        Assert.False(AppSettings.Load(session.SettingsPath).SplitView);
    }

    [Fact]
    public void SplitKeepsCardsOnTheirSideWithSpacersOpposite()
    {
        var diff = new FileDiff
        {
            Path = "a.cs",
            Hunks =
            {
                new DiffHunk
                {
                    OldStart = 1, OldCount = 3, NewStart = 1, NewCount = 3, Header = "@@ -1,3 +1,3 @@",
                    Lines =
                    {
                        new DiffLine { Kind = DiffLineKind.Context, OldNumber = 1, NewNumber = 1, Text = "a" },
                        new DiffLine { Kind = DiffLineKind.Removed, OldNumber = 2, Text = "b" },
                        new DiffLine { Kind = DiffLineKind.Removed, OldNumber = 3, Text = "c" },
                        new DiffLine { Kind = DiffLineKind.Added, NewNumber = 2, Text = "B" },
                    },
                },
            },
        };
        var unified = DiffDocument.Build(diff, [new CardAnchor("old", LineSide.Left, 3), new CardAnchor("new", LineSide.Right, 2)]);

        var split = unified.Split();

        // b pairs with B (whose card gets a spacer on the left); c has no partner, then its own card.
        Assert.Equal(["@@ -1,3 +1,3 @@", "a", "b", "￼", "c", "￼"], split.Left.Text.Split('\n'));
        Assert.Equal(["@@ -1,3 +1,3 @@", "a", "B", "￼", "", "￼"], split.Right.Text.Split('\n'));
        Assert.Equal("old", split.Left.RowAt(split.Left.CardLine("old")!.Value)!.CardId);
        Assert.Equal(split.Left.CardLine("old"), split.Right.CardLine("spacer:old"));
        Assert.Equal(split.Right.CardLine("new"), split.Left.CardLine("spacer:new"));
        Assert.Equal(DiffRowKind.Filler, split.Right.RowAt(split.Left.DocumentLineForOldLine(3)!.Value)!.Kind);
        Assert.Equal(split.Left.Rows.Count, split.Right.Rows.Count);
    }

    [AvaloniaFact]
    public async Task CtrlFFindsInTheFileAndEnterSteps()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;

        await session.PressAsync(PhysicalKey.F, RawInputModifiers.Control);
        Assert.True(pr.IsFindOpen);
        Assert.Same(session.Find<TextBox>("FindBox"), session.Window.FocusManager?.GetFocusedElement());

        session.Window.KeyTextInput("EnrichmentResult");
        await Fixture.PumpAsync();
        Assert.True(pr.FindMatches.Count > 3);
        Assert.Equal(0, pr.FindIndex);
        Assert.Equal($"1 of {pr.FindMatches.Count}", pr.FindStatus);
        Assert.Equal(pr.FindMatches, session.Find<DiffEditor>("Editor").FindMatches);
        session.Capture("29-find.png");

        await session.PressAsync(PhysicalKey.Enter);
        Assert.Equal(1, pr.FindIndex);
        await session.PressAsync(PhysicalKey.Enter, RawInputModifiers.Shift);
        Assert.Equal(0, pr.FindIndex);

        // Matches follow the file.
        await pr.SelectFileAsync(pr.Files.Single(f => f.Path.EndsWith("Program.cs", StringComparison.Ordinal)));
        Assert.All(pr.FindMatches, m => Assert.Contains("EnrichmentResult", pr.CurrentDiff!.Text.Split('\n')[m.DocumentLine - 1], StringComparison.OrdinalIgnoreCase));

        await session.PressAsync(PhysicalKey.Escape);
        Assert.False(pr.IsFindOpen);
        Assert.Empty(pr.FindMatches);
    }

    [AvaloniaFact]
    public async Task ThePaletteGoesToFilesPullRequestsAndActions()
    {
        await using var session = await AppSession.OpenAsync();
        var (vm, pr) = (session.ViewModel, session.PullRequest);

        await session.PressAsync(PhysicalKey.K, RawInputModifiers.Control);
        Assert.True(vm.Palette.IsOpen);
        Assert.Same(session.Find<TextBox>("GoToFile"), session.Window.FocusManager?.GetFocusedElement());

        session.Window.KeyTextInput("nzbn");
        await Fixture.PumpAsync(5);
        Assert.Equal("NzbnProvider.cs", vm.Palette.Selected!.Title);
        Assert.Equal(PaletteItemKind.File, vm.Palette.Selected.Kind);
        session.Capture("30-palette.png");
        await session.PressAsync(PhysicalKey.Enter);
        await Fixture.PumpAsync(5);
        Assert.False(vm.Palette.IsOpen);
        Assert.EndsWith("NzbnProvider.cs", pr.SelectedFile!.Path, StringComparison.Ordinal);

        // ">" lists actions, each with its shortcut.
        vm.Palette.Query = ">split";
        var split = Assert.Single(vm.Palette.Items);
        Assert.Equal(("Split view (side by side)", "S"), (split.Title, split.Shortcut));
        await vm.Palette.RunCommand.ExecuteAsync(null);
        Assert.True(pr.IsSplitView);

        // "#" lists pull requests from the inbox.
        vm.Palette.Query = "#timezone";
        Assert.Equal("#61 Fix timezone handling in recurring jobs", vm.Palette.Items.Single().Title);

        // Arrows move the selection.
        vm.Palette.Query = ">";
        var first = vm.Palette.Selected;
        vm.Palette.Move(+1);
        Assert.NotSame(first, vm.Palette.Selected);
        vm.Palette.Close();
    }

    [AvaloniaFact]
    public async Task WIgnoresWhitespaceInTheDiffRequest()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;

        await session.PressAsync(PhysicalKey.W);
        await Fixture.WaitUntilAsync(() => session.Source.DiffRequests.LastOrDefault()?.IgnoreWhitespace == true);

        Assert.True(pr.IgnoreWhitespace);
        Assert.Equal(pr.SelectedFile!.Path, session.Source.DiffRequests.Last().Path);
    }

    // ---------------------------------------------------------------- M8

    [AvaloniaFact]
    public async Task TheWalkthroughFramesYourOwnPullRequestAsABeforeReviewChecklist()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;
        var walkthrough = pr.Walkthrough!;

        Assert.Equal("Before you request review", pr.WalkthroughTitle);
        Assert.StartsWith("Adds contact data enrichment end to end", walkthrough.Intent, StringComparison.Ordinal);
        Assert.True(walkthrough.HasMismatch);
        Assert.Equal(8, walkthrough.Groups.Count);
        Assert.True(walkthrough.Groups[^1].IsMechanical);
        Assert.Equal(5, walkthrough.Risks.Count);
        Assert.Equal("A model's reading · glm-5.3-flash · walkthrough/v1", walkthrough.Provenance);

        // A risky area is a marker of its own shape, and a checklist item.
        var risk = walkthrough.Risks.Single(r => r.Path.EndsWith("EnrichmentScheduler.cs", StringComparison.Ordinal));
        await pr.SelectRiskAsync(risk);
        Assert.Same(risk, pr.CurrentRisk);
        Assert.Equal(risk.Path, pr.SelectedFile!.Path);
        var marker = Assert.Single(pr.Markers, m => m.Kind == "Risk");
        Assert.Equal((risk.MarkerId, 55, true), (marker.Id, marker.NewLine, marker.IsCurrent));

        pr.ToggleRiskChecked(risk);
        Assert.True(risk.IsChecked);
        Assert.True(pr.Markers.Single(m => m.Kind == "Risk").IsResolved);
        await Fixture.PumpAsync(10);
        session.Capture("31-walkthrough-own-pr.png");
    }

    [AvaloniaFact]
    public async Task GroupsSetTheReadingOrderAndJWalksThemWhenNothingIsFlagged()
    {
        await using var session = await AppSession.OpenAsync(viewer: "andrew-reviewer");
        var pr = session.PullRequest;

        pr.GroupOrder = true;
        Assert.Equal("1. Design brief", pr.Tree[0].Name);
        Assert.Equal("2. Data model and migrations", pr.Tree[1].Name);
        Assert.DoesNotContain(pr.Tree, n => n.Name.Contains("Generated", StringComparison.Ordinal));
        await Fixture.PumpAsync(5);
        session.Capture("32-reading-order.png");
        pr.GroupOrder = false;
        Assert.DoesNotContain(pr.Tree, n => n.Path.StartsWith("group:", StringComparison.Ordinal));

        // With every review point dismissed, J walks the change groups instead.
        while (pr.OpenPoints.Any())
        {
            await pr.DismissAsync(pr.OpenPoints.First());
        }

        Assert.True(pr.WalksGroups);
        await session.PressAsync(PhysicalKey.J);
        Assert.Equal("Design brief", pr.CurrentGroup!.Title);
        await session.PressAsync(PhysicalKey.J);
        Assert.Equal("Data model and migrations", pr.CurrentGroup!.Title);
        Assert.Equal("src/AndrewCrm.Web/Data/AppDbContext.cs", pr.SelectedFile!.Path);
        await session.PressAsync(PhysicalKey.K);
        Assert.Equal("Design brief", pr.CurrentGroup!.Title);
    }

    [AvaloniaFact]
    public async Task WithoutPermissionTheWalkthroughSaysWhereToTurnItOn()
    {
        var source = Fixture.CreateSource();
        await source.UpdateConnectionSettingsAsync(new ConnectionSettings { AllowCloudReasoning = true, AllowCodeToFastModel = false }, CancellationToken.None);

        var pr = await Fixture.LoadAsync(source);
        await Fixture.WaitUntilAsync(() => pr.Walkthrough is not null);

        Assert.True(pr.Walkthrough!.IsUnavailable);
        Assert.Contains("Connections", pr.Walkthrough.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(pr.Markers, m => m.Kind == "Risk");
        await pr.DisposeAsync();
    }

    // ---------------------------------------------------------------- M7

    [AvaloniaFact]
    public async Task ConnectionsStoreCloudflareCredentialsAndTheWalkthroughSwitch()
    {
        await using var session = await AppSession.OpenAsync();
        var (vm, connections) = (session.ViewModel, session.ViewModel.Connections);
        session.Source.CloudflareStored = false;
        await vm.OpenConnectionsCommand.ExecuteAsync(null);
        await Fixture.WaitUntilAsync(() => connections.IsLoaded);
        await Fixture.PumpAsync();

        Assert.Equal(ConnectionState.NotConnected, connections.FastModelState);
        Assert.False(connections.SaveCloudflareCommand.CanExecute(null));
        connections.CloudflareAccountInput = "acct-123";
        connections.CloudflareTokenInput = "s3cr3t";
        await connections.SaveCloudflareCommand.ExecuteAsync(null);

        Assert.True(session.Source.CloudflareStored);
        Assert.Equal("", connections.CloudflareTokenInput);
        Assert.Equal(ConnectionState.Connected, connections.FastModelState);
        Assert.StartsWith("Cloudflare Workers AI · Token active", connections.FastModelStatus, StringComparison.Ordinal);
        await Fixture.PumpAsync(20);
        session.Capture("33-connections-fast-model.png");

        session.Find<ToggleSwitch>("AllowCodeToFastModel").IsChecked = false;
        await Fixture.WaitUntilAsync(() => !session.Source.ConnectionSettings.AllowCodeToFastModel);

        vm.ShowSectionCommand.Execute(SettingsSection.CloudLimits);
        await Fixture.PumpAsync();
        session.Find<NumericUpDown>("FastModelDailyCap").Value = 200;
        await Fixture.WaitUntilAsync(() => session.Source.ConnectionSettings.FastModelDailyRequestCap == 200);
        connections.FastModelInput = "@cf/zai-org/glm-4.7-flash";
        await Fixture.WaitUntilAsync(() => session.Source.ConnectionSettings.FastModelId == "@cf/zai-org/glm-4.7-flash");

        await connections.RemoveCloudflareCommand.ExecuteAsync(null);
        Assert.False(session.Source.CloudflareStored);
    }

    [AvaloniaFact]
    public async Task DarkThemeRendersTheNewPanels()
    {
        var app = Application.Current!;
        try
        {
            await using var session = await AppSession.OpenAsync(viewer: "andrew-reviewer");
            var pr = session.PullRequest;
            await pr.GoToThreadAsync(pr.Threads.First(t => t.IsOpen));
            pr.IsTrayOpen = true;
            app.RequestedThemeVariant = ThemeVariant.Dark;
            await Fixture.PumpAsync(30);
            session.Capture("34-review-client-dark.png");
            pr.ShowTab(PullRequestTab.Conversation);
            await Fixture.PumpAsync(20);
            session.Capture("35-conversation-dark.png");
        }
        finally
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
        }
    }
}
