using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Lumen.App.Views;
using Lumen.Contracts;

namespace Lumen.App.Tests;

/// <summary>
/// Plan v2's definition of done: a full review of andrew-crm#58 from the inbox to a submitted Approve without opening
/// github.com, with the walkthrough shown — driven mostly from the keyboard.
/// </summary>
public sealed class FullReviewTests
{
    [AvaloniaFact]
    public async Task FromTheInboxToAnApprovalWithTheWalkthrough()
    {
        var source = new FixtureReviewSource(Fixture.DirectoryPath, viewerLogin: "andrew-reviewer");
        var settingsPath = Path.Combine(Path.GetTempPath(), "lumen-app-tests", $"{Guid.NewGuid():N}.json");
        var vm = new MainWindowViewModel(source, new AppSettings(), settingsPath);
        var window = new MainWindow { DataContext = vm };
        window.Show();

        async Task Press(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            window.KeyPressQwerty(key, modifiers);
            window.KeyReleaseQwerty(key, modifiers);
            await Fixture.PumpAsync();
        }

        try
        {
            // 1. The inbox: #58 is waiting on me, unread.
            await Fixture.WaitUntilAsync(() => !vm.Home.IsLoading);
            var item = vm.Home.Sections.Single(s => s.Id == "review-requested").Items[0];
            Assert.True(item.IsUnread);
            await vm.Home.OpenItemCommand.ExecuteAsync(item);
            var pr = vm.PullRequest!;
            await Fixture.WaitUntilAsync(() => !pr.IsAnalysing && pr.Walkthrough is { IsComplete: true } && pr.HasDetails);

            // 2. The walkthrough is the first thing shown: intent, reading order, risky areas.
            await Fixture.PumpAsync(10);
            Assert.True(window.GetVisualDescendants().OfType<WalkthroughView>().Single().IsEffectivelyVisible);
            Assert.NotEmpty(pr.Walkthrough!.Intent);

            // 3. Read the description, then the review points: examine, then comment into the review.
            pr.ShowTab(PullRequestTab.Conversation);
            Assert.True(pr.HasDescription);
            pr.ShowTab(PullRequestTab.Files);
            await Press(PhysicalKey.Enter);
            Assert.Equal(ReviewMode.Examine, pr.Mode);
            await Press(PhysicalKey.Escape);
            await Press(PhysicalKey.C);
            await Press(PhysicalKey.Enter, RawInputModifiers.Control);
            await Fixture.WaitUntilAsync(() => pr.PendingComments.Count == 1);
            await Press(PhysicalKey.J);
            await Press(PhysicalKey.D);

            // 4. A comment of my own on any line, with a suggestion.
            var risk = pr.Walkthrough.Risks[0];
            await pr.SelectRiskAsync(risk);
            var draft = pr.StartLineComment(Diff.LineSide.Right, risk.Line)!;
            PullRequestViewModel.SuggestChange(draft);
            await pr.AddDraftToReviewAsync(draft);
            Assert.Equal(2, pr.PendingComments.Count);

            // 5. Existing threads: the open one gets a reply and is resolved.
            await Press(PhysicalKey.N);
            var thread = pr.CurrentThread!;
            PullRequestViewModel.StartReply(thread);
            thread.ReplyDraft = "Thanks, that settles it.";
            await pr.ReplyAsync(thread);
            await pr.ResolveAsync(thread);
            Assert.True(thread.IsResolved);

            // 6. The checks are visible, then the review is submitted from the tray.
            pr.ShowTab(PullRequestTab.Checks);
            Assert.Equal("failure", pr.ChecksSummaryKind);
            pr.ShowTab(PullRequestTab.Files);
            await Press(PhysicalKey.R);
            Assert.True(pr.IsTrayOpen);
            pr.ReviewSummary = "Approving; the inline notes are small.";
            await pr.ApproveAsync();

            var submitted = Assert.Single(source.SubmittedReviews);
            Assert.Equal(ReviewEvent.Approve, submitted.Event);
            Assert.Empty(source.Pending.Comments);
            Assert.Equal("Approved on GitHub", pr.Toast);
            await Fixture.WaitUntilAsync(() => pr.Timeline.Any(t => t.Model.Verdict == ReviewVerdict.Approved));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        finally
        {
            window.Close();
            if (vm.PullRequest is { } open)
            {
                await open.DisposeAsync();
            }
        }
    }
}
