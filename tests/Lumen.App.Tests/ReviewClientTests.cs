using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Lumen.App.Diff;
using Lumen.App.ViewModels;
using Lumen.App.Views;
using Lumen.Contracts;

namespace Lumen.App.Tests;

/// <summary>Comment anywhere, threads and submitting a review (plan v2 M3–M5) in the real window.</summary>
public sealed class ReviewClientTests
{
    private const string ProgramPath = "src/AndrewCrm.Web/Program.cs";
    private const string CatchMeUpPath = "src/AndrewCrm.Web/Services/CatchMeUpService.cs";

    private static async Task OpenFileAsync(AppSession session, string path)
    {
        await session.PullRequest.SelectFileAsync(session.PullRequest.Files.Single(f => f.Path == path));
        await Fixture.PumpAsync(5);
    }

    // ---------------------------------------------------------------- M3

    [AvaloniaFact]
    public async Task ClickingTheGutterOpensAComposerThatCtrlEnterAddsToTheReview()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;
        await OpenFileAsync(session, ProgramPath);
        var editor = session.Find<DiffEditor>("Editor");

        // Line 229 (added) in the gutter.
        var documentLine = editor.Diff!.DocumentLineForNewLine(230)!.Value;
        await editor.RevealDocumentLineAsync(documentLine, null);
        await Fixture.PumpAsync(5);
        editor.TextView.EnsureVisualLines();
        var visual = editor.TextView.GetVisualLine(documentLine)!;
        var gutter = editor.GetVisualDescendants().OfType<DiffGutterMargin>().Single();
        var at = gutter.TranslatePoint(new Point(9, visual.VisualTop - editor.TextView.VerticalOffset + 4), session.Window)!.Value;
        session.Window.MouseMove(at);
        session.Window.MouseDown(at, MouseButton.Left);
        session.Window.MouseUp(at, MouseButton.Left);
        await Fixture.PumpAsync(10);

        var draft = Assert.Single(pr.Drafts);
        Assert.Equal((ProgramPath, LineSide.Right, 230, (int?)null), (draft.Path, draft.Side, draft.Line, draft.StartLine));
        var card = session.Window.GetVisualDescendants().OfType<DraftCardView>().Single();
        Assert.Same(card.ComposerBox, session.Window.FocusManager?.GetFocusedElement());

        session.Window.KeyTextInput("Scoped is right here.");
        await Fixture.PumpAsync();
        session.Capture("25-comment-draft.png");
        await session.PressAsync(PhysicalKey.Enter, RawInputModifiers.Control);
        await Fixture.WaitUntilAsync(() => pr.PendingComments.Count == 1);

        Assert.Empty(pr.Drafts);
        Assert.Empty(session.Source.PostedComments);
        Assert.Equal("Scoped is right here.", session.Source.Pending.Comments.Single().Body);
        Assert.Equal("Review · 1 pending", pr.TrayLabel);
        Assert.StartsWith("Added to your review", pr.Toast, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task RangesSuggestionsAndTheBaseSide()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;
        await OpenFileAsync(session, ProgramPath);

        // A range, made from the end: the draft spans 228–231 either way round.
        var range = pr.StartLineComment(LineSide.Right, 231, 228)!;
        Assert.Equal((228, 231), (range.StartLine!.Value, range.Line));
        Assert.Equal("Lines 228–231", range.RangeLabel);
        PullRequestViewModel.SuggestChange(range);
        Assert.StartsWith("```suggestion\n// Contact data enrichment", range.Body, StringComparison.Ordinal);
        Assert.EndsWith("\n```", range.Body, StringComparison.Ordinal);

        // A line outside the diff is refused, as GitHub would.
        Assert.Null(pr.StartLineComment(LineSide.Right, 5));
        Assert.Contains("isn't part of the diff", pr.Toast, StringComparison.Ordinal);

        // A deleted line comments on the base side and can't suggest.
        await OpenFileAsync(session, CatchMeUpPath);
        var removed = pr.StartLineComment(LineSide.Left, 321)!;
        Assert.Equal(LineSide.Left, removed.Side);
        Assert.False(removed.CanSuggest);
        Assert.Equal("Line 321 (before the change)", removed.RangeLabel);
        removed.Body = "Why remove this?";

        // Post now (no pending review yet): posted on its own, on the LEFT side.
        await pr.PostDraftNowAsync(removed);
        var posted = Assert.Single(session.Source.PostedComments);
        Assert.Equal((Contracts.DiffSide.Left, 321), (posted.Side, posted.Line));
        Assert.Equal("Comment posted to GitHub", pr.Toast);
    }

    [AvaloniaFact]
    public async Task PostNowWithAPendingReviewJoinsItAndSaysSo()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;
        await OpenFileAsync(session, ProgramPath);

        var first = pr.StartLineComment(LineSide.Right, 229)!;
        first.Body = "First";
        await pr.AddDraftToReviewAsync(first);
        var second = pr.StartLineComment(LineSide.Right, 230)!;
        second.Body = "Second";
        await pr.PostDraftNowAsync(second);

        Assert.Empty(session.Source.PostedComments);
        Assert.Equal(2, pr.PendingComments.Count);
        Assert.Contains("pending review", pr.Toast, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task ReviewPointCommentsAddToTheReviewOrPostNowFromTheKeyboard()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;
        var point = pr.CurrentPoint!;

        await session.PressAsync(PhysicalKey.C);
        await session.PressAsync(PhysicalKey.Enter, RawInputModifiers.Control);
        await Fixture.WaitUntilAsync(() => point.IsCommented);
        Assert.Equal(point.Id, session.Source.Pending.Comments.Single().ReviewPointId);
        Assert.Empty(session.Source.PostedComments);

        // Ctrl+Shift+Enter posts at once — here into the pending review, as GitHub insists.
        await pr.NextPointAsync();
        var next = pr.CurrentPoint!;
        await session.PressAsync(PhysicalKey.C);
        await session.PressAsync(PhysicalKey.Enter, RawInputModifiers.Control | RawInputModifiers.Shift);
        await Fixture.WaitUntilAsync(() => next.IsCommented);
        Assert.Equal(2, pr.PendingComments.Count);
    }

    // ---------------------------------------------------------------- M4

    [AvaloniaFact]
    public async Task ThreadsShowInlineWithCountsAndNWalksTheOpenOnes()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;

        Assert.Equal(3, pr.Threads.Count);
        Assert.Equal("2 open threads", pr.ThreadsLabel);
        Assert.Equal(1, pr.Files.Single(f => f.Path == ProgramPath).ThreadCount);
        Assert.Equal(1, pr.Files.Single(f => f.Path == CatchMeUpPath).ThreadCount);
        Assert.Equal(0, pr.Files.Single(f => f.Path == Fixture.TopPointPath).ThreadCount);

        // The resolved thread starts collapsed under its line.
        var resolved = pr.Threads.Single(t => t.IsResolved);
        Assert.True(resolved.IsCollapsed);
        Assert.NotNull(session.Find<DiffEditor>("Editor").Diff!.CardLine(resolved.CardId));

        await session.PressAsync(PhysicalKey.N);
        Assert.Equal(ProgramPath, pr.SelectedFile!.Path);
        Assert.Equal("PRRT_1", pr.CurrentThread!.Id);
        await Fixture.PumpAsync(10);
        var card = session.Window.GetVisualDescendants().OfType<ThreadCardView>().Single(c => ((ThreadCardContext)c.DataContext!).Thread.Id == "PRRT_1");
        Assert.True(card.IsEffectivelyVisible);
        session.Capture("26-thread.png");

        // The outdated thread leads its file.
        await session.PressAsync(PhysicalKey.N);
        Assert.Equal(CatchMeUpPath, pr.SelectedFile!.Path);
        var outdated = pr.CurrentThread!;
        Assert.True(outdated.IsOutdated);
        Assert.Equal(1, session.Find<DiffEditor>("Editor").Diff!.CardLine(outdated.CardId));

        await session.PressAsync(PhysicalKey.N, RawInputModifiers.Shift);
        Assert.Equal("PRRT_1", pr.CurrentThread!.Id);
    }

    [AvaloniaFact]
    public async Task RepliesAndResolvingGoThroughGitHub()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;
        var thread = pr.Threads.Single(t => t.Id == "PRRT_1");
        await pr.GoToThreadAsync(thread);
        await Fixture.PumpAsync(10);

        PullRequestViewModel.StartReply(thread);
        await Fixture.PumpAsync();
        var box = session.Window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ReplyBox" && t.IsEffectivelyVisible);
        box.Focus();
        session.Window.KeyTextInput("Agreed.");
        await session.PressAsync(PhysicalKey.Enter, RawInputModifiers.Control);
        await Fixture.WaitUntilAsync(() => thread.Comments.Count == 3);
        Assert.Equal("Agreed.", thread.Comments[2].Body);
        Assert.False(thread.IsReplying);
        Assert.Equal("Reply posted to GitHub", pr.Toast);

        await pr.ResolveAsync(thread);
        Assert.True(thread.IsResolved);
        Assert.True(thread.IsCollapsed);
        Assert.Equal("1 open thread", pr.ThreadsLabel);
        Assert.Equal(0, pr.Files.Single(f => f.Path == ProgramPath).ThreadCount);

        await pr.UnresolveAsync(thread);
        Assert.False(thread.IsResolved);
    }

    [AvaloniaFact]
    public async Task TheConversationLinksThreadsIntoTheFilesTab()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;
        pr.ShowTab(PullRequestTab.Conversation);
        await Fixture.PumpAsync(5);

        var link = session.Find<ItemsControl>("ThreadLinks").GetVisualDescendants().OfType<Button>().First();
        Assert.NotNull(link.Command);
        link.BringIntoView();
        await Fixture.PumpAsync(5);
        await session.ClickAsync(link);
        await Fixture.PumpAsync(5);

        Assert.Equal(PullRequestTab.Files, pr.Tab);
        Assert.Equal(pr.Threads[0].Path, pr.SelectedFile!.Path);
    }

    // ---------------------------------------------------------------- M5

    [AvaloniaFact]
    public async Task TheTrayEditsDeletesAndSubmitsAnApproval()
    {
        await using var session = await AppSession.OpenAsync(viewer: "andrew-reviewer");
        var pr = session.PullRequest;
        Assert.True(pr.CanApprove);
        Assert.Equal("Walkthrough", pr.WalkthroughTitle);
        await OpenFileAsync(session, ProgramPath);
        foreach (var (line, body) in new[] { (229, "One"), (230, "Two") })
        {
            var draft = pr.StartLineComment(LineSide.Right, line)!;
            draft.Body = body;
            await pr.AddDraftToReviewAsync(draft);
        }

        await session.PressAsync(PhysicalKey.R);
        Assert.True(pr.IsTrayOpen);
        Assert.Equal(2, pr.PendingComments.Count);

        var first = pr.PendingComments[0];
        PullRequestViewModel.EditPending(first);
        first.EditBody = "One, edited";
        await pr.SavePendingAsync(first);
        Assert.Equal("One, edited", session.Source.Pending.Comments[0].Body);
        await pr.DeletePendingAsync(pr.PendingComments[1]);
        Assert.Single(pr.PendingComments);
        await Fixture.PumpAsync(10);
        session.Capture("27-review-tray.png");

        await pr.JumpToPendingAsync(pr.PendingComments[0]);
        Assert.Equal(ProgramPath, pr.SelectedFile!.Path);

        pr.ReviewSummary = "Looks good once the GUID is in Key Vault.";
        await session.ClickAsync(session.Find<Button>("Approve"));
        await Fixture.WaitUntilAsync(() => session.Source.SubmittedReviews.Count == 1);

        var submitted = session.Source.SubmittedReviews.Single();
        Assert.Equal((ReviewEvent.Approve, "Looks good once the GUID is in Key Vault."), (submitted.Event, submitted.Body));
        await Fixture.WaitUntilAsync(() => !pr.HasPendingReview);
        Assert.Empty(pr.PendingComments);
        Assert.False(pr.IsTrayOpen);
        Assert.Equal("Approved on GitHub", pr.Toast);

        // The approval is the new "since last review" baseline: nothing newer to show.
        await Fixture.WaitUntilAsync(() => pr.Details!.LastReviewedSha == "2baaca76d26ccd0b73159e1e9c11a97dc73c360d");
        Assert.False(pr.CanShowSinceLastReview);
    }

    [AvaloniaFact]
    public async Task OnYourOwnPullRequestOnlyCommentReviewsAreOffered()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;
        Assert.True(pr.ViewerIsAuthor);
        Assert.False(pr.CanApprove);

        pr.IsTrayOpen = true;
        await Fixture.PumpAsync(5);
        Assert.False(session.Find<Button>("Approve").IsEffectivelyEnabled);
        Assert.False(session.Find<Button>("RequestChanges").IsEffectivelyEnabled);

        await pr.SubmitCommentAsync();
        Assert.Equal("Write a summary or add a comment first.", pr.SubmitError);

        pr.ReviewSummary = "Self-review notes.";
        await pr.SubmitCommentAsync();
        Assert.Equal(ReviewEvent.Comment, session.Source.SubmittedReviews.Single().Event);
    }
}
