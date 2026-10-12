using Lumen.Domain;

namespace Lumen.GitHub.Tests;

/// <summary>
/// Creates and deletes a pending review on a scratch pull request (opt in: <c>LUMEN_LIVE_PENDING_PR=owner/repo#n</c>,
/// with <c>gh auth login</c> or <c>LUMEN_GITHUB_TOKEN</c>). Never submits: a pending review is visible only to its
/// author, and it is deleted before the test ends. Run only with the repository owner's go-ahead.
/// </summary>
public sealed class LivePendingReviewTests
{
    [Fact]
    public async Task StartAddReadAndDeleteAPendingReview()
    {
        if (!PullRequestKey.TryParse(Environment.GetEnvironmentVariable("LUMEN_LIVE_PENDING_PR"), out var key))
        {
            return;
        }

        using var http = new HttpClient();
        using var tokens = new GhCliTokenSource();
        var client = new GitHubRestClient(http, tokens);
        var info = await client.GetPullRequestAsync(key, CancellationToken.None);
        var before = await client.GetReviewThreadsAsync(key, CancellationToken.None);
        Assert.Null(before.PendingReview);

        var reviewId = await client.StartPendingReviewAsync(key, info.HeadSha, CancellationToken.None);
        try
        {
            var during = await client.GetReviewThreadsAsync(key, CancellationToken.None);
            Assert.Equal(reviewId, during.PendingReview?.Id);

            var details = await client.GetPullRequestDetailsAsync(key, CancellationToken.None);
            Assert.Equal(info.HeadSha, details.HeadSha);
        }
        finally
        {
            await client.DeletePendingReviewAsync(reviewId, CancellationToken.None);
        }

        var after = await client.GetReviewThreadsAsync(key, CancellationToken.None);
        Assert.Null(after.PendingReview);
    }
}
