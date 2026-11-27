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
    public async Task FromTheInboxToAnApproval()
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
            await Fixture.WaitUntilAsync(() => !pr.IsAnalysing && pr.HasDetails);

            // 2. The change opens on its files with proven-mechanical changes folded.
            await Fixture.PumpAsync(10);
            Assert.True(pr.HasTriage);
            Assert.True(pr.MechanicalLines > 0);

            // 3. Read the description, then comment into the review from the keyboard (C, then Ctrl+Enter).
            pr.ShowTab(PullRequestTab.Conversation);
            Assert.True(pr.HasDescription);
            pr.ShowTab(PullRequestTab.Files);
            await Fixture.WaitUntilAsync(() => pr.CurrentDiff is { Changes.Count: > 0 });
            await Press(PhysicalKey.C);
            Assert.Single(pr.Drafts);
            pr.Drafts[0].Body = "Is this covered by a test?";
            await pr.AddDraftToReviewAsync(pr.Drafts[0]);
            await Fixture.WaitUntilAsync(() => pr.PendingComments.Count == 1);

            // 4. A comment of my own on any line, with a suggestion.
            var line = pr.CurrentDiff!.Rows.Last(r => r.Kind == Diff.DiffRowKind.Added).NewNumber;
            var draft = pr.StartLineComment(Diff.LineSide.Right, line)!;
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
