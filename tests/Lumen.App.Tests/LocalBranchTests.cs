using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Lumen.App.Views;
using Lumen.Contracts;

namespace Lumen.App.Tests;

/// <summary>Phase 2: local branches on the inbox, opening one from the go-to box, and posting controls hidden.</summary>
public sealed class LocalBranchTests
{
    [Fact]
    public void TheGoToBoxTakesACloneFolderWithAnOptionalBranchAndBase()
    {
        var dir = Directory.CreateTempSubdirectory("lumen-local-").FullName;
        try
        {
            var tree = MainWindowViewModel.TryParseLocal(dir)!;
            var branch = MainWindowViewModel.TryParseLocal($"{dir}@feature/x")!;
            var ranged = MainWindowViewModel.TryParseLocal($"\"{dir}@feature/x..develop\"")!;

            Assert.Equal((dir, true, "", ""), (tree.ClonePath, tree.WorkingTree, tree.HeadRef, tree.BaseRef));
            Assert.Equal((false, "feature/x", ""), (branch.WorkingTree, branch.HeadRef, branch.BaseRef));
            Assert.Equal(("feature/x", "develop"), (ranged.HeadRef, ranged.BaseRef));
            Assert.Null(MainWindowViewModel.TryParseLocal("KhaiStimpson/andrew-crm#58"));
            Assert.Null(MainWindowViewModel.TryParseLocal(Path.Combine(dir, "missing")));
            Assert.Null(MainWindowViewModel.TryParseLocal("relative/folder"));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [AvaloniaFact]
    public async Task TheInboxListsLocalBranchesAndOpensOneReadOnly()
    {
        var source = new GrpcLikeReviewSource(Fixture.CreateSource());
        var settingsPath = Path.Combine(Path.GetTempPath(), "lumen-app-tests", $"{Guid.NewGuid():N}.json");
        var vm = new MainWindowViewModel(source, new AppSettings(), settingsPath);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            await Fixture.WaitUntilAsync(() => !vm.Home.IsLoading && vm.Home.HasLocalBranches);
            var local = vm.Home.LocalBranches;
            Assert.Equal(2, local.Count);
            Assert.Equal("feature/ai-change", local[0].Title);
            Assert.Equal("C:/work/contoso-shop · against main", local[0].Detail);
            Assert.Equal("3,101 changed → 2,342 to read", local[0].ShrinkLabel);
            Assert.Equal("claude/refactor-pricing-rules", local[1].Title);
            Assert.Equal("Not analysed yet", local[1].ShrinkLabel);
            Assert.True(window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "LocalBranches").IsEffectivelyVisible);

            await vm.Home.OpenLocalItemCommand.ExecuteAsync(local[1]);

            var request = Assert.Single(source.WatchRequests);
            Assert.Equal(("C:/Dev/work/billing", "claude/refactor-pricing-rules", "develop", false),
                (request.Local.ClonePath, request.Local.HeadRef, request.Local.BaseRef, request.Local.WorkingTree));
            Assert.True(vm.PullRequest!.IsLocal);
            Assert.False(vm.PullRequest.CanPost);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheRecordedLocalReviewShowsSlicesFlagsAndWhatChangedSinceTheLastLook()
    {
        var source = new FixtureReviewSource(Path.Combine(AppContext.BaseDirectory, "fixtures", "razor-pages-app-local"));
        var local = new LocalBranchRef { ClonePath = "C:/work/contoso-shop", BaseRef = "main", HeadRef = "feature/ai-change" };
        var pr = new PullRequestViewModel(source, new WatchPullRequestRequest { Local = local });
        pr.Start();
        try
        {
            await Fixture.WaitUntilAsync(() => !pr.IsAnalysing && pr.HasSlices);
            Assert.True(pr.IsLocal);
            Assert.False(pr.CanPost);
            Assert.Equal(9, pr.SliceGroups.SelectMany(g => g.Slices).Count(s => s.Kind == SliceKind.Feature));
            Assert.Equal(17, pr.Flags.Count);

            // Orders / Edit was marked before the follow-up push touched it.
            var edit = pr.SliceGroups.SelectMany(g => g.Slices).Single(s => s.Title == "Orders / Edit");
            await Fixture.WaitUntilAsync(() => edit.ChangedSinceMark);
            Assert.Equal("Changed after review", edit.StateLabel);

            // The follow-up commit: three files, two flags fixed and one new.
            pr.ShowSinceLastLook = true;
            await Fixture.WaitUntilAsync(() => pr.FixedFlags.Count > 0);
            Assert.Equal(2, pr.FixedFlags.Count);
            Assert.Single(pr.Flags, f => f.ChangeLabel == "New");
            Assert.False(pr.IsHistoryRewritten);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ALocalSnapshotHidesPostingAndUsesTheEnginesRef()
    {
        var source = Fixture.CreateSource();
        var local = new LocalBranchRef { ClonePath = "C:/Dev/work/contoso-shop", BaseRef = "main", WorkingTree = true };
        var pr = new PullRequestViewModel(source, new WatchPullRequestRequest { Local = local });
        try
        {
            Assert.Equal("working tree vs main", pr.Title);
            var synthetic = new PullRequestRef { Owner = "local", Name = "contoso-shop-1a2b3c4d", Number = 0 };
            await pr.ApplyAsync(new PullRequestEvent
            {
                Snapshot = new PullRequestSnapshot
                {
                    PullRequest = synthetic,
                    Title = "feature/x (working tree) vs main",
                    State = "local",
                    Local = local,
                    TargetKey = "local:c:/Dev/work/contoso-shop|main|WorkingTree",
                    Files = { new ChangedFileSummary { Path = "src/A.cs", Additions = 3 } },
                },
            });

            Assert.Same(synthetic, pr.Ref);
            Assert.True(pr.IsLocal);
            Assert.False(pr.CanPost);
            Assert.Equal("Local", pr.NumberLabel);
            Assert.Equal("Local branch", pr.ReviewKindLabel);
            Assert.Equal("Read-only", pr.StateLabel);

            // Viewed ticks are GitHub's; a local branch never sends one.
            await pr.ToggleViewedCommand.ExecuteAsync(pr.Files[0]);
            Assert.False(pr.Files[0].IsViewed);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [Fact]
    public void InboxRowsShowChangedAloneUntilAnalysedAndMarkAMovedHead()
    {
        var now = DateTimeOffset.UtcNow;
        InboxItemViewModel Row(ReviewProgress? progress) =>
            new(new InboxItem { PullRequest = Fixture.PullRequest, Additions = 4000, Deletions = 212, Progress = progress }, now);

        var fresh = Row(null);
        var analysed = Row(new ReviewProgress { Analysed = true, ChangedLines = 4212, ToReadLines = 1180, SlicesTotal = 8, SlicesReviewed = 3 });
        var moved = Row(new ReviewProgress { Analysed = true, ChangedLines = 4212, ToReadLines = 1180, OutOfDate = true });
        var done = Row(new ReviewProgress { Analysed = true, ChangedLines = 36, ToReadLines = 22, SlicesTotal = 1, SlicesReviewed = 1 });

        Assert.Equal(("4,212 changed", false, ""), (fresh.ShrinkLabel, fresh.IsOutOfDate, fresh.SlicesLabel));
        Assert.Equal(("4,212 changed → 1,180 to read", false, "3 of 8 slices reviewed"), (analysed.ShrinkLabel, analysed.IsOutOfDate, analysed.SlicesLabel));
        Assert.True(moved.IsOutOfDate);
        Assert.Equal("✓ 1 of 1 slices reviewed", done.SlicesLabel);
    }
}
