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
