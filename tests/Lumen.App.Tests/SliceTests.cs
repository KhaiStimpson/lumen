using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumen.App.Diff;
using Lumen.App.ViewModels;
using Lumen.Contracts;

namespace Lumen.App.Tests;

/// <summary>Phase 4 and 6 in the app: the slice list, one slice at a time in reading order, marks, and the fallback.</summary>
public sealed class SliceTests
{
    /// <summary>A small slicing of the andrew-crm#58 recording: one page slice, Shared and Unlinked.</summary>
    internal static SlicesReady Slices(PullRequestViewModel pr)
    {
        var files = pr.Files.Where(f => !f.IsMechanical && f.Model.Additions + f.Model.Deletions > 0).Select(f => f.Path).ToList();
        var ready = new SlicesReady { DurationMs = 12 };
        var page = new Slice { Id = "s-page", Title = "Contacts / Enrich", Kind = SliceKind.Feature, SeedPath = files[0] };
        page.Files.Add(new SliceFile { Path = files[2], Order = 0, Why = "`EnrichModel` uses `ContactDto`" });
        page.Files.Add(new SliceFile { Path = files[1], Order = 2, Why = "`EnrichModel` injects `IEnrichmentService` → `EnrichmentService`" });
        page.Files.Add(new SliceFile { Path = files[0], Order = 3, Why = "page `Contacts/Enrich`" });
        var other = new Slice { Id = "s-other", Title = "Contacts / Index", Kind = SliceKind.Feature, SeedPath = files[3] };
        other.Files.Add(new SliceFile { Path = files[3], Order = 3, Why = "page `Contacts/Index`" });
        var shared = new Slice { Id = "s-shared", Title = "Shared", Kind = SliceKind.Shared };
        shared.Files.Add(new SliceFile { Path = files[4], Order = 2, Why = "used by 3 slices" });
        var unlinked = new Slice { Id = "s-unlinked", Title = "Unlinked · nothing uses it", Kind = SliceKind.UnlinkedUnused };
        unlinked.Files.Add(new SliceFile { Path = files[5], Order = 2, Why = "nothing uses it" });
        ready.Slices.AddRange([page, other, shared, unlinked]);
        ready.Edges.Add(new SliceEdge { From = files[0], To = files[1], Reason = "`EnrichModel` injects `IEnrichmentService` → `EnrichmentService`" });
        ready.Units.AddRange(files.Take(6).Select(f => new UnitFingerprint { Unit = f, Path = f, Fingerprint = "fp-" + f.Length, OpenLines = 10 }));
        return ready;
    }

    private static async Task<(PullRequestViewModel Pr, Services.FixtureReviewSource Source)> LoadSlicedAsync()
    {
        var source = Fixture.CreateSource();
        var pr = await Fixture.LoadAsync(source);
        source.Publish(new PullRequestEvent { SlicesReady = Slices(pr) });
        await Fixture.WaitUntilAsync(() => pr.HasSlices && pr.SelectedFile is not null);
        return (pr, source);
    }

    [AvaloniaFact]
    public async Task SlicesBecomeTheLeftPaneWithTheFirstPageOpenInReadingOrder()
    {
        var (pr, _) = await LoadSlicedAsync();
        try
        {
            Assert.True(pr.HasSlices);
            Assert.False(pr.ShowsFileTree);
            Assert.Equal(["Pages", "Across slices"], pr.SliceGroups.Select(g => g.Title));
            Assert.Equal("Contacts / Enrich", pr.CurrentSlice!.Title);
            Assert.Equal(pr.CurrentSlice.Files[0].Path, pr.SelectedFile!.Path);
            Assert.Equal("1", pr.CurrentSliceFile!.PositionLabel);
            Assert.Equal("`EnrichModel` uses `ContactDto`", pr.CurrentSliceFile.Why);
            Assert.Equal("4", pr.SliceCountLabel);
            Assert.Contains("0 of 4 slices reviewed", pr.SliceProgressLabel, StringComparison.Ordinal);

            // The file tree is one toggle away.
            pr.ToggleFileList();
            Assert.True(pr.ShowsFileTree);
            pr.ToggleFileList();
            Assert.False(pr.ShowsFileTree);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task NextFileFollowsTheReadingOrderAndCarriesOnIntoTheNextSlice()
    {
        var (pr, _) = await LoadSlicedAsync();
        try
        {
            var order = pr.CurrentSlice!.Files.Select(f => f.Path).ToList();
            await pr.NextFileAsync();
            Assert.Equal(order[1], pr.SelectedFile!.Path);
            Assert.Equal("2 of 3", pr.FilePositionLabel);
            await pr.NextFileAsync();
            await pr.NextFileAsync();
            Assert.Equal("Contacts / Index", pr.CurrentSlice!.Title);

            await pr.PreviousFileAsync();
            Assert.Equal("Contacts / Enrich", pr.CurrentSlice!.Title);
            Assert.Equal(order[^1], pr.SelectedFile!.Path);

            await pr.NextSliceAsync();
            await pr.NextSliceAsync();
            Assert.Equal("Shared", pr.CurrentSlice!.Title);
            await pr.PreviousSliceAsync();
            Assert.Equal("Contacts / Index", pr.CurrentSlice!.Title);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task MarkReviewedMarksEveryUnitAndAPushThatChangesOneClearsOnlyItsSlice()
    {
        var (pr, source) = await LoadSlicedAsync();
        try
        {
            var slice = pr.CurrentSlice!;
            await pr.MarkSliceReviewedAsync();

            var request = Assert.Single(source.MarkRequests);
            Assert.True(request.Marked);
            Assert.Equal(slice.Files.Select(f => f.Path).Order(), request.Units.Order());
            Assert.True(slice.IsReviewed);
            Assert.Equal("✓ Reviewed", slice.StateLabel);
            Assert.Equal("Contacts / Index", pr.CurrentSlice!.Title);
            Assert.Contains("1 of 4 slices reviewed", pr.SliceProgressLabel, StringComparison.Ordinal);

            // A push changes one of its files: that file's fingerprint no longer matches, so the slice says so.
            var marks = new MarksUpdated();
            marks.Marks.AddRange(request.Units.Select((u, i) => new ReviewMark { Unit = u, Fingerprint = i == 0 ? "stale" : "fp-" + u.Length }));
            await pr.ApplyAsync(new PullRequestEvent { MarksUpdated = marks });

            Assert.False(slice.IsReviewed);
            Assert.True(slice.ChangedSinceMark);
            Assert.Equal("Changed after review", slice.StateLabel);
            Assert.Contains("1 changed since you marked it", pr.SliceProgressLabel, StringComparison.Ordinal);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task MarkReviewedSaysWhatIsStillOpenAndNeedsASecondPress()
    {
        var (pr, source) = await LoadSlicedAsync();
        try
        {
            var path = pr.CurrentSlice!.Files[1].Path;
            var flags = new FlagsReady();
            flags.Flags.Add(new RedFlag { Id = "f1", Kind = FlagKind.GuardRemoved, Path = path, Side = DiffSide.Right, Line = 1, Message = "A null check was removed." });
            await pr.ApplyAsync(new PullRequestEvent { FlagsReady = flags });

            await pr.MarkSliceReviewedAsync();
            Assert.True(pr.MarkReviewed.IsArmed);
            Assert.Equal("1 flag", pr.MarkReviewed.Hint);
            Assert.Equal("Press again to mark reviewed", pr.MarkReviewed.Label);
            Assert.Empty(source.MarkRequests);

            await pr.MarkSliceReviewedAsync();
            Assert.Single(source.MarkRequests);
            Assert.False(pr.MarkReviewed.IsArmed);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task FlagsSitUnderTheirLineAndGStepsThroughThemInReadingOrder()
    {
        var (pr, _) = await LoadSlicedAsync();
        try
        {
            var first = pr.CurrentSlice!.Files[0].Path;
            var line = pr.CurrentDiff!.Rows.First(r => r.Kind == DiffRowKind.Added).NewNumber;
            var flags = new FlagsReady();
            flags.Flags.Add(new RedFlag { Id = "f-raw", Kind = FlagKind.RawOutput, Path = first, Side = DiffSide.Right, Line = line, Message = "`@Html.Raw` writes `Model.Notes` without encoding." });
            flags.Flags.Add(new RedFlag { Id = "f-sql", Kind = FlagKind.RawSql, Path = pr.CurrentSlice.Files[1].Path, Side = DiffSide.Right, Line = 3, Message = "`FromSqlRaw` with an interpolated string." });
            await pr.ApplyAsync(new PullRequestEvent { FlagsReady = flags });

            Assert.Equal(2, pr.CurrentSlice.FlagCount);
            Assert.NotNull(pr.CurrentDiff!.CardLine("flag:f-raw"));
            Assert.Equal("Raw output", pr.ResolveFlagCard("flag:f-raw")!.Flag.KindLabel);
            Assert.Contains(pr.Markers, m => m.Kind == "flag" && m.NewLine == line);

            await pr.NextFlagAsync();
            Assert.Equal("f-raw", pr.CurrentFlag!.Id);
            await pr.NextFlagAsync();
            Assert.Equal("f-sql", pr.CurrentFlag!.Id);
            Assert.Equal(pr.CurrentSlice.Files[1].Path, pr.SelectedFile!.Path);
            await pr.PreviousFlagAsync();
            Assert.Equal("f-raw", pr.CurrentFlag!.Id);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task MechanicalOnlyListsItsGroupsNotItsFiles()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateRefactorSource());
        try
        {
            var group = pr.Triage!.Groups.First(g => pr.Triage.Hunks.Any(h => h.GroupId == g.Id && h.Fold == FoldKind.Mechanical));
            var members = pr.Triage.Hunks.Where(h => h.GroupId == group.Id && h.Fold == FoldKind.Mechanical).Select(h => h.Path).Distinct().ToList();
            var ready = new SlicesReady();
            var slice = new Slice { Id = "m", Title = "Mechanical only", Kind = SliceKind.MechanicalOnly };
            slice.Files.AddRange(members.Select((p, i) => new SliceFile { Path = p, Order = i, Why = "every change is proven mechanical" }));
            ready.Slices.Add(slice);
            await pr.ApplyAsync(new PullRequestEvent { SlicesReady = ready });
            await pr.SelectSliceAsync(pr.Slices.Single());

            Assert.True(pr.IsMechanicalSlice);
            Assert.Contains($"{group.Title} · {group.MemberCount} site{(group.MemberCount == 1 ? "" : "s")}", pr.MechanicalGroups);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task AFlagCardMarkedNewRendersWithoutWritingItsLabelBack()
    {
        // Run.Text binds two-way by default; with a StringFormat it once wrote "  · New" back into the label, forever.
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        var window = new Avalonia.Controls.Window();
        try
        {
            var flag = new FlagViewModel(new RedFlag { Id = "f1", Kind = FlagKind.RawOutput, Path = "a.cs", Line = 1, Message = "`@Html.Raw(` writes without encoding." }) { ChangeLabel = "New" };
            window.Content = new Views.FlagCardView { DataContext = new FlagCardContext(pr, flag) };
            window.Show();
            window.UpdateLayout();

            Assert.Equal("New", flag.ChangeLabel);
            var text = window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().First().Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text);
            Assert.Equal("Flag  Raw output  · New", string.Concat(text));
        }
        finally
        {
            window.Close();
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task WhenSlicingFallsBackTheFileListStaysAndSaysWhy()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            await pr.ApplyAsync(new PullRequestEvent { SlicesReady = new SlicesReady { FellBack = true, FallbackReason = "No changed page, controller or endpoint: showing the file list." } });

            Assert.False(pr.HasSlices);
            Assert.True(pr.ShowsFileTree);
            Assert.Equal("No changed page, controller or endpoint: showing the file list.", pr.SliceFallbackReason);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task MarkReviewedCountsBoilerplateFoldsNobodyOpenedUntilTheyAreOpened()
    {
        var source = Fixture.CreateAuditSource();
        var pr = await Fixture.LoadAsync(source);
        try
        {
            // Make one folded hunk a boilerplate fold: summarised, not proven, so somebody should open it.
            var triage = pr.Triage!.Clone();
            var hunk = triage.Hunks.First(DiffFolds.Folds);
            hunk.Fold = FoldKind.Boilerplate;
            await pr.ApplyAsync(new PullRequestEvent { TriageReady = triage });
            var ready = new SlicesReady();
            var slice = new Slice { Id = "s", Title = "Audit", Kind = SliceKind.Feature, SeedPath = hunk.Path };
            slice.Files.Add(new SliceFile { Path = hunk.Path, Order = 1, Why = "entry point" });
            ready.Slices.Add(slice);
            ready.Units.Add(new UnitFingerprint { Unit = hunk.Path, Path = hunk.Path, Fingerprint = "fp", OpenLines = 3 });
            await pr.ApplyAsync(new PullRequestEvent { SlicesReady = ready });
            await Fixture.WaitUntilAsync(() => pr.SelectedFile?.Path == hunk.Path && pr.CurrentDiff is not null);

            await pr.MarkSliceReviewedAsync();
            Assert.True(pr.MarkReviewed.IsArmed);
            Assert.Equal("1 fold not opened", pr.MarkReviewed.Hint);
            Assert.Empty(source.MarkRequests);

            // Opening the slice's folds clears the hint: the next press marks at once.
            pr.MarkReviewed.IsArmed = false;
            pr.OpenSliceFolds();
            await pr.MarkSliceReviewedAsync();
            Assert.False(pr.MarkReviewed.IsArmed);
            Assert.Equal([hunk.Path], Assert.Single(source.MarkRequests).Units);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task OpenAllFoldsInTheSliceOpensEveryFoldInItsFiles()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateAuditSource());
        try
        {
            var folded = pr.Triage!.Hunks.Where(DiffFolds.Folds).GroupBy(h => h.Path).First();
            var ready = new SlicesReady();
            var slice = new Slice { Id = "s", Title = "Audit", Kind = SliceKind.Feature, SeedPath = folded.Key };
            slice.Files.Add(new SliceFile { Path = folded.Key, Order = 1, Why = "entry point" });
            ready.Slices.Add(slice);
            await pr.ApplyAsync(new PullRequestEvent { SlicesReady = ready });
            await Fixture.WaitUntilAsync(() => pr.SelectedFile?.Path == folded.Key && pr.CurrentDiff is not null);

            pr.OpenSliceFolds();

            Assert.All(folded, h => Assert.True(pr.IsFoldOpened(DiffFolds.IdFor(h))));
            Assert.All(folded, h => Assert.True(pr.ResolveFold(DiffFold.CardPrefix + DiffFolds.IdFor(h))!.IsExpanded));
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task WhileSlicingRunsTheFileListSaysSoAndCountsUpUntilTheSlicesArrive()
    {
        var source = Fixture.CreateSource();
        var pr = await Fixture.LoadAsync(source);
        var window = new Avalonia.Controls.Window { Content = new Views.PullRequestView { DataContext = pr }, Width = 1280, Height = 800 };
        window.Show();
        try
        {
            Assert.False(pr.IsSlicing);
            source.Publish(new PullRequestEvent { Progress = new AnalysisProgress { Stage = "slices", Message = "Building feature slices: linking files…" } });
            await Fixture.WaitUntilAsync(() => pr.IsSlicing);
            Assert.True(pr.ShowsFileTree);
            Assert.Equal("Building feature slices: linking files…", pr.SlicingStatus);
            Assert.Equal("0:00", pr.SlicingElapsedLabel);
            var banner = window.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Single(b => b.Name == "SlicingBanner");
            Assert.True(banner.IsEffectivelyVisible);

            // Another kind of progress leaves it alone; the next step updates the text, not the clock.
            source.Publish(new PullRequestEvent { Progress = new AnalysisProgress { Stage = "slices", Message = "Building feature slices: grouping changes…" } });
            await Fixture.WaitUntilAsync(() => pr.SlicingStatus.EndsWith("grouping changes…", StringComparison.Ordinal));
            Assert.True(pr.IsSlicing);

            source.Publish(new PullRequestEvent { SlicesReady = Slices(pr) });
            await Fixture.WaitUntilAsync(() => pr.HasSlices);
            Assert.False(pr.IsSlicing);
            Assert.False(banner.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
            await pr.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59, "0:59")]
    [InlineData(61, "1:01")]
    [InlineData(725, "12:05")]
    public void SlicingTimeReadsAsMinutesAndSeconds(int seconds, string label) =>
        Assert.Equal(label, PullRequestViewModel.FormatElapsed(TimeSpan.FromSeconds(seconds)));
}
