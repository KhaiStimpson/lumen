using Avalonia.Headless.XUnit;
using Lumen.App.Diff;
using Lumen.App.ViewModels;
using Lumen.Contracts;

namespace Lumen.App.Tests;

/// <summary>
/// Guided-review behaviour over the recorded andrew-crm#58 session. These run on the Avalonia dispatcher because the
/// view model resumes on the captured context (ConfigureAwait(true)); without one, fixture echo events would be applied
/// on the thread pool concurrently with the test.
/// </summary>
public sealed class PullRequestViewModelTests
{
    [AvaloniaFact]
    public async Task FailedFileSelectionClearsThePreviousFilesCode()
    {
        var source = new GrpcLikeReviewSource(Fixture.CreateSource());
        var pr = await Fixture.LoadAsync(source);
        try
        {
            Assert.NotNull(pr.CurrentDiff);
            var next = pr.Files.First(f => f.Path != pr.SelectedFile!.Path);
            source.FailDiffPath = next.Path;

            await pr.SelectFileAsync(next);

            Assert.Same(next, pr.SelectedFile);
            Assert.Null(pr.CurrentDiff);
            Assert.Empty(pr.Markers);
            Assert.Null(pr.FocusRange);
            Assert.False(pr.IsDiffLoading);
            Assert.Contains("Diff unavailable", pr.Toast, StringComparison.Ordinal);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task RefreshReplacesSelectionAndClearsOldTriage()
    {
        var source = new GrpcLikeReviewSource(Fixture.CreateSource());
        var pr = await Fixture.LoadAsync(source);
        try
        {
            var oldFile = pr.SelectedFile!;
            var remaining = pr.Files.First(f => f.Path != oldFile.Path && !f.IsMechanical);
            source.RefreshEvents =
            [
                new PullRequestEvent { Snapshot = new PullRequestSnapshot { Files = { remaining.Model.Clone() } } },
                new PullRequestEvent { Complete = new AnalysisComplete() },
            ];

            await pr.ReanalyseCommand.ExecuteAsync(null);
            await Fixture.WaitUntilAsync(() => !pr.IsAnalysing);

            Assert.Null(pr.Triage);
            Assert.False(pr.HasSlices);
            Assert.Empty(pr.Flags);
            Assert.Equal("", pr.SliceProgressLabel);
            Assert.Equal(remaining.Path, pr.SelectedFile?.Path);
            Assert.Same(Assert.Single(pr.Files), pr.SelectedFile);
            Assert.Equal(remaining.Path, pr.CurrentDiff?.Path);
            Assert.DoesNotContain(oldFile, pr.Files);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task LoadsFilesAndBuildsCompactTreeWithoutMechanicalFiles()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            Assert.Equal(60, pr.Files.Count);
            Assert.Equal(14, pr.GeneratedFiles.Count);
            Assert.All(pr.GeneratedFiles, f => Assert.True(f.IsMechanical));

            var nodes = pr.Tree.SelectMany(n => n.Descendants().Prepend(n)).ToList();
            var fileNodes = nodes.Where(n => n.File is not null).ToList();
            Assert.Equal(46, fileNodes.Count);
            Assert.DoesNotContain(fileNodes, n => n.File!.IsMechanical);
            Assert.Contains(nodes, n => n.IsFolder && n.Name.Contains('/', StringComparison.Ordinal));
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ReplayCarriesTheTriage()
    {
        var pr = await Fixture.LoadAsync(new GrpcLikeReviewSource(Fixture.CreateSource()));
        try
        {
            var triage = pr.Triage;
            Assert.NotNull(triage);
            Assert.Equal((19_743, 15_864), (triage.Summary.TotalLines, triage.Summary.MechanicalLines));
            Assert.Equal(60, triage.Files.Count);
            Assert.Contains(triage.Hunks, h => h.ChangeClass == ChangeClass.Generated && h.Fold == FoldKind.Mechanical);
            Assert.Contains(triage.Hunks, h => h.ChangeClass == ChangeClass.ImportsOnly && h.Fold == FoldKind.Mechanical);
            Assert.Equal(triage.Summary.TotalLines, triage.Files.Sum(f => f.FoldLines.Open + f.FoldLines.Mechanical + f.FoldLines.Boilerplate));
            Assert.Equal("3,879 lines to read of 19,743 changed", pr.ShrinkLabel);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task OpeningAFileDoesNotTickItButToggleViewedDoes()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            Assert.NotNull(pr.SelectedFile);
            Assert.All(pr.Files, f => Assert.False(f.IsViewed));
            Assert.Equal("0 / 60 viewed", pr.ViewedSummary);

            var file = pr.SelectedFile;
            await pr.ToggleViewedCommand.ExecuteAsync(file);
            await Fixture.PumpAsync();

            Assert.True(file.IsViewed);
            Assert.Equal("1 / 60 viewed", pr.ViewedSummary);

            await pr.ToggleViewedCommand.ExecuteAsync(file);
            await Fixture.PumpAsync();

            Assert.False(file.IsViewed);
            Assert.Equal("0 / 60 viewed", pr.ViewedSummary);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ViewedTickIsPutBackWhenGitHubRefusesIt()
    {
        var source = new GrpcLikeReviewSource(Fixture.CreateSource()) { FailViewedWith = "GitHub didn't save it: Resource not accessible" };
        var pr = await Fixture.LoadAsync(source);
        try
        {
            var path = pr.Triage!.Hunks.First(h => h.Fold == FoldKind.Open && h.Path != pr.SelectedFile!.Path).Path;
            var file = pr.Files.Single(f => f.Path == path);

            await pr.ToggleViewedCommand.ExecuteAsync(file);

            Assert.False(file.IsViewed);
            Assert.Equal("0 / 60 viewed", pr.ViewedSummary);
            Assert.Contains("Resource not accessible", pr.Toast, StringComparison.Ordinal);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task SnapshotViewedStateFromGitHubIsShown()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            var path = pr.Files[1].Path;

            await pr.ApplyAsync(new PullRequestEvent { FileViewed = new FileViewed { Path = path, Viewed = true } });

            Assert.True(pr.Files[1].IsViewed);
            Assert.Equal("1 / 60 viewed", pr.ViewedSummary);
            Assert.True(new FileEntryViewModel(new ChangedFileSummary { Path = path, IsViewed = true }).IsViewed);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ReanalysingCancelsTheLiveStreamWithoutFailing()
    {
        // The fixture's watch never ends on its own, so re-analysing always cancels a live stream, as with the engine.
        var pr = await Fixture.LoadAsync(new GrpcLikeReviewSource(Fixture.CreateSource()));
        try
        {
            pr.NeedsReanalysis = true;

            await pr.ReanalyseCommand.ExecuteAsync(null);
            await Fixture.WaitUntilAsync(() => !pr.IsAnalysing);

            Assert.Null(pr.Error);
            Assert.False(pr.NeedsReanalysis);
            Assert.NotNull(pr.Triage);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task FullFileToggleShowsWholeFileAndKeepsCardsUnderTheirLines()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            await pr.SelectFileAsync(pr.Files.Single(f => f.Path == Fixture.TopPointPath));
            var hunks = pr.CurrentDiff!;
            var line = hunks.Rows.First(r => r.Kind == DiffRowKind.Added).NewNumber;
            var draft = pr.StartLineComment(LineSide.Right, line)!;
            hunks = pr.CurrentDiff!;
            Assert.Contains(hunks.Rows, r => r.Kind == DiffRowKind.HunkHeader);

            pr.ShowFullFile = true;
            await Fixture.WaitUntilAsync(() => pr.CurrentDiff != hunks);

            var full = pr.CurrentDiff!;
            Assert.Equal(Fixture.TopPointPath, full.Path);
            Assert.DoesNotContain(full.Rows, r => r.Kind == DiffRowKind.HunkHeader);
            var shown = full.Rows.Where(r => r.NewNumber > 0).Select(r => r.NewNumber).ToList();
            Assert.Equal(Enumerable.Range(1, shown.Count), shown);
            Assert.Equal(hunks.Additions, full.Additions);
            Assert.Equal(hunks.Deletions, full.Deletions);
            Assert.Equal(full.DocumentLineForNewLine(line) + 1, full.CardLine(draft.Id));

            pr.ShowFullFile = false;
            Assert.Equal(hunks.Text, pr.CurrentDiff!.Text);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task FileFilterNarrowsTheTree()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            pr.FileFilter = "Enrichment";

            var files = pr.Tree.SelectMany(n => n.Descendants().Prepend(n)).Where(n => n.File is not null).ToList();
            var expected = pr.Files.Count(f => !f.IsMechanical && f.Path.Contains("Enrichment", StringComparison.OrdinalIgnoreCase));
            Assert.NotEmpty(files);
            Assert.Equal(expected, files.Count);
            Assert.True(files.Count < 46);
            Assert.All(files, n => Assert.Contains("Enrichment", n.Path, StringComparison.OrdinalIgnoreCase));

            pr.FileFilter = "";
            Assert.Equal(46, pr.Tree.SelectMany(n => n.Descendants().Prepend(n)).Count(n => n.File is not null));
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    private static DiffRow CodeRowAbove(DiffDocument diff, int cardLine)
    {
        var line = cardLine - 1;
        while (diff.RowAt(line) is { Kind: DiffRowKind.Card })
        {
            line--;
        }

        return diff.RowAt(line)!;
    }
}
