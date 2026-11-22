using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumen.App.Markdown;
using Lumen.App.ViewModels;
using Lumen.App.Views;
using Lumen.Contracts;

namespace Lumen.App.Tests;

/// <summary>Pull request context (plan v2 M2): header, conversation, commits, since last review, checks.</summary>
public sealed class ContextTests
{
    private const string LastReviewed = "4c8e1a7b2d9f3e6a5b0c7d8e9f1a2b3c4d5e6f70";

    [AvaloniaFact]
    public async Task HeaderShowsMergeabilityLabelsAndReviewers()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;

        Assert.Equal("Blocked by required reviews or checks", pr.MergeabilityLabel);
        Assert.Equal("pending", pr.MergeabilityKind);
        Assert.Equal(["enhancement", "needs review"], pr.Labels.Select(l => l.Name));
        Assert.Equal("#a2eeef", pr.Labels[0].Color);
        Assert.Equal(["andrew-reviewer", "sam-ops"], pr.Reviewers.Select(r => r.Login));
        Assert.Equal("Requested", pr.Reviewers[0].StateLabel);
        Assert.Equal("Commented", pr.Reviewers[1].StateLabel);
        Assert.True(session.Find<TextBlock>("Mergeability").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task ConversationShowsDescriptionTimelineAndPostsATopLevelComment()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;

        await session.ClickAsync(session.Find<RadioButton>("TabConversation"));
        Assert.Equal(PullRequestTab.Conversation, pr.Tab);
        Assert.True(session.Find<ConversationView>("ConversationTab").IsEffectivelyVisible);
        Assert.False(session.Find<Grid>("FilesTab").IsVisible);
        Assert.Equal("Conversation · 3", pr.ConversationLabel);
        Assert.Equal(["commented", "reviewed", "commented"], pr.Timeline.Select(t => t.ActionLabel));
        Assert.Equal("2 inline comments", pr.Timeline[1].CommentCountLabel);
        Assert.True(session.Find<MarkdownView>("Description").IsEffectivelyVisible);
        await Fixture.PumpAsync(10);
        session.Capture("21-conversation.png");

        var box = session.Find<TextBox>("IssueComment");
        box.Focus();
        session.Window.KeyTextInput("Thanks — merging after the **ABR GUID** is set.");
        await session.PressAsync(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.Control);
        await Fixture.WaitUntilAsync(() => pr.Timeline.Count == 4);

        Assert.Equal(["Thanks — merging after the **ABR GUID** is set."], session.Source.IssueComments);
        Assert.Equal("", pr.IssueCommentDraft);
        Assert.Equal("Comment posted to GitHub", pr.Toast);
    }

    [AvaloniaFact]
    public async Task ACommitLimitsTheFilesTabAndShowAllComesBack()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;
        Assert.Equal("Commits · 3", pr.CommitsLabel);

        // The middle commit: its files only, and review points (head lines) stay out of it.
        await pr.SelectCommitAsync(pr.Commits[1], extend: false);
        Assert.Equal(PullRequestTab.Files, pr.Tab);
        Assert.True(pr.HasScope);
        Assert.False(pr.ScopeEndsAtHead);
        Assert.StartsWith("Commit 4c8e1a7 · Enrichment providers", pr.ScopeLabel, StringComparison.Ordinal);
        Assert.Equal(29, pr.ScopeFiles.Count);
        Assert.DoesNotContain(pr.ScopeFiles, f => f.Path.EndsWith("Program.cs", StringComparison.Ordinal));
        Assert.Empty(pr.Markers);
        var request = session.Source.DiffRequests.Last();
        Assert.Equal(("9f1e2d3c4b5a69788796a5b4c3d2e1f00a1b2c3d", LastReviewed), (request.Range.FromSha, request.Range.ToSha));
        await Fixture.PumpAsync(10);
        Assert.True(session.Find<Border>("ScopeBanner").IsEffectivelyVisible);
        session.Capture("22-commit-scope.png");

        // Shift-picks the head: a range ending at the head, so review points come back.
        await pr.SelectCommitAsync(pr.Commits[2], extend: true);
        Assert.Equal("2 commits · 4c8e1a7…2baaca7", pr.ScopeLabel);
        Assert.True(pr.ScopeEndsAtHead);
        Assert.True(pr.Commits[1].IsSelected && pr.Commits[2].IsSelected && !pr.Commits[0].IsSelected);

        await pr.ClearScopeAsync();
        Assert.False(pr.HasScope);
        Assert.Equal(60, pr.Files.Count);
        Assert.All(pr.Commits, c => Assert.False(c.IsSelected));
    }

    [AvaloniaFact]
    public async Task SinceLastReviewShowsTheDeltaAndItsWalkthrough()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;
        Assert.True(pr.CanShowSinceLastReview);
        Assert.Equal("Since your last review (4c8e1a7)", pr.LastReviewedLabel);

        await session.ClickAsync(session.Find<Avalonia.Controls.Primitives.ToggleButton>("SinceLastReview"));
        await Fixture.WaitUntilAsync(() => pr.HasScope && pr.DeltaWalkthrough is not null);

        Assert.True(pr.ShowSinceLastReview);
        Assert.True(pr.Scope!.IsSinceLastReview);
        Assert.Equal(27, pr.ScopeFiles.Count);
        Assert.Equal(LastReviewed, session.Source.WalkthroughRequests.Single().Since.FromSha);
        Assert.StartsWith("Since you last looked: 27 files", pr.DeltaWalkthrough!.Intent, StringComparison.Ordinal);
        await Fixture.PumpAsync(10);
        session.Capture("23-since-last-review.png");

        pr.ShowSinceLastReview = false;
        await Fixture.WaitUntilAsync(() => !pr.HasScope);
    }

    [AvaloniaFact]
    public async Task ChecksListFailuresFirstWithDurations()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;

        pr.ShowTab(PullRequestTab.Checks);
        await Fixture.PumpAsync(5);

        Assert.Equal(["test", "codeql", "build"], pr.Checks.Select(c => c.Name));
        Assert.Equal("failure", pr.ChecksSummaryKind);
        Assert.Equal("Checks · 1/3", pr.ChecksLabel);
        Assert.Equal("7m 31s", pr.Checks[0].DurationLabel);
        Assert.Equal("In Progress", pr.Checks[1].StateLabel);
        Assert.True(session.Find<ChecksView>("ChecksTab").IsEffectivelyVisible);
        session.Capture("24-checks.png");
    }

    [AvaloniaFact]
    public async Task ANewPushOffersAReload()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;

        await pr.ApplyAsync(new PullRequestEvent { HeadChanged = new HeadChanged { OldSha = "2baaca76d26ccd0b", NewSha = "77aa11ee22ff3344" } });
        await Fixture.PumpAsync();

        Assert.Equal("New commits were pushed (2baaca7 → 77aa11e). Reload to review them.", pr.NewHeadLabel);
        Assert.True(session.Find<Border>("NewHeadBanner").IsEffectivelyVisible);
    }

    [Fact]
    public void MarkdownParsesTheBlocksReviewsUse()
    {
        var blocks = MarkdownParser.Parse("""
            ## What it does

            Adds **providers** and `IEnrichmentProvider`, see [docs](https://x.io).

            - one
            - [x] done
            1. first

            > quoted

            ```csharp
            var x = 1;
            ```

            ```suggestion
            return 2;
            ```
            ---
            """);

        Assert.IsType<HeadingBlock>(blocks[0]);
        Assert.Equal(2, ((HeadingBlock)blocks[0]).Level);
        var paragraph = Assert.IsType<ParagraphBlock>(blocks[1]);
        Assert.Contains(MarkdownParser.Inlines(paragraph.Text), i => i.Bold && i.Text == "providers");
        Assert.Contains(MarkdownParser.Inlines(paragraph.Text), i => i.Code && i.Text == "IEnrichmentProvider");
        Assert.Contains(MarkdownParser.Inlines(paragraph.Text), i => i.Link == "https://x.io" && i.Text == "docs");
        var list = Assert.IsType<ListBlock>(blocks[2]);
        Assert.Equal(["one", "done"], list.Items);
        Assert.Equal([null, true], list.Checked);
        Assert.IsType<ListBlock>(blocks[3]);
        Assert.Equal("quoted", Assert.IsType<QuoteBlock>(blocks[4]).Text);
        Assert.Equal(("csharp", "var x = 1;"), (((CodeBlock)blocks[5]).Language, ((CodeBlock)blocks[5]).Code));
        Assert.Equal("return 2;", MarkdownParser.Suggestion("x\n```suggestion\nreturn 2;\n```"));
        Assert.IsType<RuleBlock>(blocks[^1]);
    }
}
