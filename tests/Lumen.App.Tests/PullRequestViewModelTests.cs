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
            Assert.NotEmpty(pr.ReviewPoints);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task RanksReviewPointsAndTagsThemInOrder()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            Assert.Equal(3, pr.ReviewPoints.Count);

            var top = pr.ReviewPoints[0];
            Assert.Equal(Fixture.TopPointTitle, top.Title);
            Assert.Equal("Medium", top.Severity);
            Assert.Equal(Fixture.TopPointPath, top.Path);
            Assert.Equal(Fixture.TopPointLine, top.Line);
            Assert.Equal(5, top.Locations.Count);

            Assert.Equal(["R1", "R2", "R3"], pr.ReviewPoints.Select(p => p.Tag));
            Assert.Equal([1, 2, 3], pr.ReviewPoints.Select(p => p.Ordinal));

            // Severity first, then the engine's priority.
            var ranked = pr.ReviewPoints
                .OrderByDescending(p => p.Model.Severity)
                .ThenByDescending(p => p.Model.Priority)
                .ToList();
            Assert.Equal(ranked, pr.ReviewPoints);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ArrivesAtTopPointWithInlineCardUnderAnchorLine()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            var point = pr.ReviewPoints[0];
            Assert.Same(point, pr.CurrentPoint);
            Assert.True(point.IsCurrent);
            Assert.Equal(Fixture.TopPointPath, pr.SelectedFile?.Path);
            Assert.Equal("1 of 3", pr.PositionLabel);

            var diff = pr.CurrentDiff;
            Assert.NotNull(diff);
            Assert.Equal(Fixture.TopPointPath, diff.Path);

            var cardLine = diff.CardLine(point.Id);
            Assert.NotNull(cardLine);
            Assert.Equal(diff.DocumentLineForNewLine(point.Line) + 1, cardLine);
            var row = diff.RowAt(cardLine.Value);
            Assert.NotNull(row);
            Assert.Equal(DiffRowKind.Card, row.Kind);
            Assert.Equal(point.Id, row.CardId);
            Assert.Equal(point.Line, diff.RowAt(cardLine.Value - 1)!.NewNumber);

            Assert.Contains(pr.Markers, m => m.Id == point.Id);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task OtherLocationsGetCompactReferenceCards()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            var point = pr.ReviewPoints[0];
            var others = point.Locations.Where(l => !l.IsPrimary).ToList();
            Assert.Equal(4, others.Count);

            foreach (var location in others)
            {
                var file = pr.Files.Single(f => f.Path == location.Path);
                await pr.SelectFileAsync(file);

                var diff = pr.CurrentDiff;
                Assert.NotNull(diff);
                Assert.Equal(location.Path, diff.Path);

                var id = $"{point.Id}@{location.Path}:{location.Line}";
                Assert.Equal(id, PullRequestViewModel.CardId(point, location));
                var cardLine = diff.CardLine(id);
                Assert.True(cardLine is not null, $"No reference card for {id}");
                Assert.Equal(DiffRowKind.Card, diff.RowAt(cardLine.Value)!.Kind);
                Assert.Equal(location.Line, CodeRowAbove(diff, cardLine.Value).NewNumber);

                var resolved = pr.ResolveCard(id);
                Assert.NotNull(resolved);
                Assert.Same(point, resolved.Value.Point);
                Assert.False(resolved.Value.IsPrimary);
            }
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task NextAndPreviousCycleThroughOpenPointsWithWrapAround()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            var points = pr.ReviewPoints.ToList();

            await pr.NextPointAsync();
            Assert.Same(points[1], pr.CurrentPoint);
            Assert.Equal("2 of 3", pr.PositionLabel);
            Assert.Equal(points[1].Path, pr.SelectedFile?.Path);
            Assert.False(points[0].IsCurrent);
            Assert.True(points[1].IsCurrent);

            await pr.NextPointAsync();
            Assert.Same(points[2], pr.CurrentPoint);
            Assert.Equal("3 of 3", pr.PositionLabel);

            await pr.NextPointAsync();
            Assert.Same(points[0], pr.CurrentPoint);
            Assert.Equal("1 of 3", pr.PositionLabel);

            await pr.PreviousPointAsync();
            Assert.Same(points[2], pr.CurrentPoint);
            Assert.Equal("3 of 3", pr.PositionLabel);

            await pr.PreviousPointAsync();
            Assert.Same(points[1], pr.CurrentPoint);
            Assert.Equal("2 of 3", pr.PositionLabel);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task DismissMovesOnAndRestoreBringsThePointBack()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            var points = pr.ReviewPoints.ToList();

            await pr.DismissAsync(null);
            Assert.Equal(ReviewPointState.Dismissed, points[0].State);
            Assert.True(points[0].IsDismissed);
            Assert.Same(points[1], pr.CurrentPoint);
            Assert.Equal(2, pr.OpenPoints.Count());
            Assert.DoesNotContain(points[0], pr.OpenPoints);
            Assert.Equal("1 of 2", pr.PositionLabel);
            Assert.DoesNotContain(pr.Markers, m => m.Id == points[0].Id);

            // The fixture echoes the state change back through the event stream; it must stay dismissed.
            await Fixture.PumpAsync();
            Assert.True(points[0].IsDismissed);

            // Dismissed points are skipped by navigation.
            await pr.NextPointAsync();
            Assert.Same(points[2], pr.CurrentPoint);
            await pr.NextPointAsync();
            Assert.Same(points[1], pr.CurrentPoint);

            await pr.RestoreAsync(points[0]);
            Assert.Equal(ReviewPointState.Visible, points[0].State);
            Assert.Equal(3, pr.OpenPoints.Count());
            Assert.Contains(points[0], pr.OpenPoints);
            Assert.Equal("2 of 3", pr.PositionLabel);

            await Fixture.PumpAsync();
            Assert.Equal(ReviewPointState.Visible, points[0].State);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task PostingACommentSendsOneRequestForTheAnchor()
    {
        var source = Fixture.CreateSource();
        var pr = await Fixture.LoadAsync(source);
        try
        {
            var point = pr.CurrentPoint!;
            Assert.False(string.IsNullOrWhiteSpace(point.SuggestedComment));

            point.CommentDraft = "";
            pr.StartComment(null);
            Assert.True(point.IsComposing);
            Assert.Equal(point.SuggestedComment, point.CommentDraft);

            point.CommentDraft = "  " + point.SuggestedComment + "\n\n";
            await pr.PostCommentAsync(null);

            var request = Assert.Single(source.PostedComments);
            Assert.Equal(Fixture.TopPointPath, request.Path);
            Assert.Equal(Fixture.TopPointLine, request.Line);
            Assert.Equal(point.SuggestedComment.Trim(), request.Body);
            Assert.Equal(point.Id, request.ReviewPointId);
            Assert.Equal(Fixture.PullRequest, request.PullRequest);

            Assert.Equal(ReviewPointState.Commented, point.State);
            Assert.True(point.IsCommented);
            Assert.False(point.IsComposing);
            Assert.False(point.IsPosting);
            Assert.NotNull(point.PostedUrl);

            await Fixture.PumpAsync();
            Assert.Equal(ReviewPointState.Commented, point.State);
            Assert.Single(source.PostedComments);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ExamineShowsEvidenceAndClosesBackToDiff()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            Assert.Equal(ReviewMode.Diff, pr.Mode);

            await pr.ExamineAsync(null);
            Assert.Equal(ReviewMode.Examine, pr.Mode);
            Assert.True(pr.IsExamining);

            await pr.ShowEvidenceAsync(null);
            Assert.Equal("evidence", pr.ExamineTab);
            Assert.True(pr.IsEvidenceTab);
            Assert.Equal(ReviewMode.Examine, pr.Mode);

            await Fixture.PumpAsync();
            Assert.Equal(ReviewPointState.Examined, pr.CurrentPoint!.State);

            pr.CloseExamine();
            Assert.Equal(ReviewMode.Diff, pr.Mode);
            Assert.False(pr.IsExamining);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task InvestigationProgressAndEvidenceArriveWithoutDisturbingTheReview()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            var point = pr.CurrentPoint!;
            var completeStatus = pr.Status;

            await pr.ApplyAsync(new PullRequestEvent { InvestigationStatus = new InvestigationStatus { Active = 2 } });
            Assert.Equal("Analysing 2 areas…", pr.Status);

            var updated = point.Model.Clone();
            updated.EvidenceState = EvidenceState.Strong;
            updated.Evidence.Add(new Evidence
            {
                Id = "inv-1-0",
                Source = "AgentInvestigation",
                Producer = "claude-code · pattern-investigator/v1 · claude-sonnet-5",
                Summary = "ChargeWorker claims before charging (src/ChargeWorker.cs:9)",
                Location = new CodeLocation { Path = "src/ChargeWorker.cs", StartLine = 9, EndLine = 9 },
            });
            await pr.ApplyAsync(new PullRequestEvent { ReviewPointUpdated = updated });

            Assert.Same(point, pr.CurrentPoint);
            Assert.Equal(3, pr.ReviewPoints.Count);
            Assert.Equal("Strong repository precedent", point.EvidenceLabel);
            var agent = point.Evidence[^1];
            Assert.Equal("Agent investigation · claude-code · pattern-investigator/v1 · claude-sonnet-5", agent.Provenance);

            await pr.ApplyAsync(new PullRequestEvent { InvestigationStatus = new InvestigationStatus { Active = 0 } });
            Assert.Equal(completeStatus, pr.Status);

            await pr.ApplyAsync(new PullRequestEvent { InvestigationStatus = new InvestigationStatus { Active = 0, Detail = "Not signed in" } });
            Assert.Equal($"{completeStatus} · Not signed in", pr.Status);
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
