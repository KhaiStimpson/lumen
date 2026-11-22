using System.Net;
using System.Text.Json;
using Lumen.Domain;

namespace Lumen.GitHub.Tests;

/// <summary>The review client's GraphQL and REST calls against recorded payloads (plan v2 M1–M5).</summary>
public sealed class GitHubReviewClientTests : IDisposable
{
    private static readonly PullRequestKey Key = new(new RepositoryRef("octo", "widgets"), 42);

    private readonly FakeHttpMessageHandler _handler = new();
    private readonly HttpClient _http;
    private readonly GitHubRestClient _client;

    public GitHubReviewClientTests()
    {
        _http = new HttpClient(_handler);
        _client = new GitHubRestClient(_http, new StaticTokenSource("t0ken"));
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }

    private static string Payload(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Payloads", name));

    private static JsonElement Variables(RecordedRequest request) =>
        JsonDocument.Parse(request.Body!).RootElement.GetProperty("variables");

    private static string Query(RecordedRequest request) =>
        JsonDocument.Parse(request.Body!).RootElement.GetProperty("query").GetString()!;

    [Fact]
    public async Task InboxSearchesEveryQueryInOneRequestAndMapsItems()
    {
        _handler.Respond(HttpStatusCode.OK, Payload("inbox.json"));

        var result = await _client.SearchInboxAsync(
            [new InboxQuery("review-requested", "is:open is:pr review-requested:@me"), new InboxQuery("mine", "is:open is:pr author:@me")],
            CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://api.github.com/graphql", request.Uri.ToString());
        Assert.Equal("is:open is:pr review-requested:@me", Variables(request).GetProperty("q0").GetString());
        Assert.Equal("is:open is:pr author:@me", Variables(request).GetProperty("q1").GetString());
        Assert.Contains("s1: search(query: $q1, type: ISSUE", Query(request), StringComparison.Ordinal);

        Assert.Equal("mona", result.ViewerLogin);
        var requested = Assert.Single(result.Results["review-requested"]);
        Assert.Equal(new PullRequestKey(new RepositoryRef("octo", "crm"), 58), requested.Key);
        Assert.Equal("hubot", requested.Author);
        Assert.Equal((19700, 120, 60), (requested.Additions, requested.Deletions, requested.ChangedFiles));
        Assert.Equal(InboxCheckState.Failure, requested.Checks);
        Assert.Equal(InboxReviewState.Awaiting, requested.Review);

        var mine = Assert.Single(result.Results["mine"]);
        Assert.True(mine.IsDraft);
        Assert.Equal(InboxCheckState.None, mine.Checks);
        Assert.Equal(InboxReviewState.None, mine.Review);

        Assert.Equal(4980, _client.RateLimit!.Remaining);
    }

    [Fact]
    public async Task DetailsMapConversationCommitsChecksAndReviewers()
    {
        _handler.Respond(HttpStatusCode.OK, Payload("details.json"));

        var details = await _client.GetPullRequestDetailsAsync(Key, CancellationToken.None);

        Assert.Equal("Adds **sprockets**.", details.Body);
        Assert.Equal(Mergeability.Conflicting, details.Mergeability);
        Assert.Equal("DIRTY", details.MergeState);
        Assert.Equal("head333", details.HeadSha);
        Assert.Equal([new PullRequestLabel("enhancement", "a2eeef")], details.Labels);

        // The pending review is the viewer's draft, never part of the conversation.
        Assert.Equal(["IC_1", "PRR_1", "PRR_3"], details.Timeline.Select(t => t.Id));
        Assert.Equal(ReviewVerdict.ChangesRequested, details.Timeline[1].Verdict);
        Assert.Equal(2, details.Timeline[1].ReviewCommentCount);
        Assert.Equal("ghost", details.Timeline[2].Author);

        Assert.Equal(["c1", "c2"], details.Commits.Select(c => c.Sha));
        Assert.Equal(["hubot", "Someone"], details.Commits.Select(c => c.Author));

        Assert.Equal(
            [CheckState.Success, CheckState.Pending, CheckState.Failure, CheckState.Failure],
            details.Checks.Select(c => c.State));
        Assert.True(details.Checks[3].IsStatus);
        Assert.Equal("Preview failed", details.Checks[3].Detail);

        Assert.Contains(details.Reviewers, r => r is { Login: "mona", Verdict: ReviewVerdict.ChangesRequested, IsRequested: false });
        Assert.Contains(details.Reviewers, r => r is { Login: "octocat", IsRequested: true, Verdict: null });
        Assert.Contains(details.Reviewers, r => r is { Login: "octo/core", IsTeam: true });
        Assert.Equal("head222", details.ViewerLastReviewedSha);
        Assert.Equal("mona", details.ViewerLogin);
    }

    [Fact]
    public async Task ThreadsIncludeOutdatedAndPendingWithThePendingReviewBuiltFromThem()
    {
        _handler.Respond(HttpStatusCode.OK, Payload("threads.json"));

        var result = await _client.GetReviewThreadsAsync(Key, CancellationToken.None);

        Assert.Equal(2, result.Threads.Count);
        var outdated = result.Threads[0];
        Assert.True(outdated.IsOutdated);
        Assert.Null(outdated.Line);
        Assert.Equal(3, outdated.OriginalLine);
        Assert.Equal(DiffSide.Left, outdated.Side);
        Assert.Equal("head111", outdated.OriginalCommitSha);
        Assert.Equal("@@ -1,3 +1,2 @@", outdated.DiffHunk);
        Assert.False(outdated.IsPendingOnly);
        Assert.True(outdated.Comments[1].IsPending);

        var draft = result.Threads[1];
        Assert.True(draft.IsPendingOnly);
        Assert.Equal((10, 12), (draft.StartLine, draft.Line));

        var pending = result.PendingReview!;
        Assert.Equal("PRR_pending", pending.Id);
        Assert.Equal("Draft summary", pending.Body);
        Assert.Equal(2, pending.Comments.Count);
        Assert.Equal(new PendingComment { Id = "C_p1", ThreadId = "T_2", Path = "src/Foo.cs", Side = DiffSide.Right, Line = 12, StartLine = 10, Body = "New thought" }, pending.Comments[0]);
        Assert.True(pending.Comments[1].IsReply);
        Assert.Equal("T_1", pending.Comments[1].ThreadId);
        Assert.Equal(DiffSide.Left, pending.Comments[1].Side);
    }

    [Fact]
    public async Task ThreadsFollowPagination()
    {
        var first = Payload("threads.json").Replace(
            "\"pageInfo\": { \"hasNextPage\": false, \"endCursor\": null }",
            "\"pageInfo\": { \"hasNextPage\": true, \"endCursor\": \"CUR\" }",
            StringComparison.Ordinal);
        _handler.Respond(HttpStatusCode.OK, first).Respond(HttpStatusCode.OK, Payload("threads.json"));

        var result = await _client.GetReviewThreadsAsync(Key, CancellationToken.None);

        Assert.Equal(4, result.Threads.Count);
        Assert.False(Variables(_handler.Requests[0]).TryGetProperty("after", out _));
        Assert.Equal("CUR", Variables(_handler.Requests[1]).GetProperty("after").GetString());
    }

    [Fact]
    public async Task RangeCommentPostsStartLineAndSides()
    {
        _handler.Respond(HttpStatusCode.Created, """{"id":5,"html_url":"https://github.com/x#r5"}""");

        await _client.PostReviewCommentAsync(
            new NewReviewComment(Key, "head222", "src/Foo.cs", 9, "Range", DiffSide.Left, 7, DiffSide.Left),
            CancellationToken.None);

        Assert.Equal(
            """{"body":"Range","commit_id":"head222","path":"src/Foo.cs","line":9,"side":"LEFT","start_line":7,"start_side":"LEFT"}""",
            _handler.Requests[0].Body);
    }

    [Fact]
    public async Task SingleCommentRefusedForAPendingReviewSaysSo()
    {
        _handler.Respond(
            HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed: user has a pending review on this pull request"}""");

        await Assert.ThrowsAsync<PendingReviewExistsException>(() => _client.PostReviewCommentAsync(
            new NewReviewComment(Key, "head222", "src/Foo.cs", 9, "x"),
            CancellationToken.None));
    }

    [Fact]
    public async Task PendingReviewLifecycleUsesTheExpectedMutations()
    {
        _handler
            .Respond(HttpStatusCode.OK, """{"data":{"repository":{"pullRequest":{"id":"PR_kw42"}}}}""")
            .Respond(HttpStatusCode.OK, """{"data":{"addPullRequestReview":{"pullRequestReview":{"id":"PRR_new"}}}}""")
            .Respond(HttpStatusCode.OK, """{"data":{"addPullRequestReviewThread":{"thread":{"id":"T_9","comments":{"nodes":[{"id":"C_9"}]}}}}}""")
            .Respond(HttpStatusCode.OK, """{"data":{"updatePullRequestReviewComment":{"pullRequestReviewComment":{"id":"C_9"}}}}""")
            .Respond(HttpStatusCode.OK, """{"data":{"deletePullRequestReviewComment":{"pullRequestReview":{"id":"PRR_new"}}}}""")
            .Respond(HttpStatusCode.OK, """{"data":{"submitPullRequestReview":{"pullRequestReview":{"id":"PRR_new","url":"https://github.com/r/1"}}}}""");

        var review = await _client.StartPendingReviewAsync(Key, "head333", CancellationToken.None);
        var comment = await _client.AddPendingThreadAsync(
            review,
            new NewReviewComment(Key, "head333", "src/Foo.cs", 12, "Hmm", DiffSide.Right, 10, DiffSide.Right),
            CancellationToken.None);
        await _client.UpdatePendingCommentAsync(comment, "Hmm!", CancellationToken.None);
        await _client.DeletePendingCommentAsync(comment, CancellationToken.None);
        var submitted = await _client.SubmitReviewAsync(review, ReviewEvent.Approve, "LGTM", CancellationToken.None);

        Assert.Equal(("PRR_new", "C_9"), (review, comment));
        Assert.Equal(new SubmittedReview("PRR_new", "https://github.com/r/1"), submitted);

        var start = Variables(_handler.Requests[1]);
        Assert.Equal("PR_kw42", start.GetProperty("pullRequestId").GetString());
        Assert.Equal("head333", start.GetProperty("commitOID").GetString());

        var thread = Variables(_handler.Requests[2]).GetProperty("input");
        Assert.Equal("PRR_new", thread.GetProperty("pullRequestReviewId").GetString());
        Assert.Equal(12, thread.GetProperty("line").GetInt32());
        Assert.Equal(10, thread.GetProperty("startLine").GetInt32());
        Assert.Equal("RIGHT", thread.GetProperty("side").GetString());

        Assert.Contains("updatePullRequestReviewComment", Query(_handler.Requests[3]), StringComparison.Ordinal);
        Assert.Contains("deletePullRequestReviewComment", Query(_handler.Requests[4]), StringComparison.Ordinal);
        var submit = Variables(_handler.Requests[5]).GetProperty("input");
        Assert.Equal("APPROVE", submit.GetProperty("event").GetString());
        Assert.Equal("LGTM", submit.GetProperty("body").GetString());
    }

    [Fact]
    public async Task PullRequestIdIsRememberedFromEarlierReads()
    {
        _handler
            .Respond(HttpStatusCode.OK, Payload("threads.json"))
            .Respond(HttpStatusCode.OK, """{"data":{"addPullRequestReview":{"pullRequestReview":{"id":"PRR_new"}}}}""");

        await _client.GetReviewThreadsAsync(Key, CancellationToken.None);
        await _client.StartPendingReviewAsync(Key, "head333", CancellationToken.None);

        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal("PR_kw42", Variables(_handler.Requests[1]).GetProperty("pullRequestId").GetString());
    }

    [Fact]
    public async Task ReplyGoesIntoThePendingReviewWhenGiven()
    {
        _handler.Respond(HttpStatusCode.OK, """
            {"data":{"addPullRequestReviewThreadReply":{"comment":{"id":"C_r","databaseId":7,"author":{"login":"mona"},"body":"Yes","createdAt":"2026-10-05T00:00:00Z","url":"u","pullRequestReview":{"id":"PRR_pending","state":"PENDING"}}}}}
            """);

        var reply = await _client.ReplyToThreadAsync("T_1", "Yes", "PRR_pending", CancellationToken.None);

        Assert.True(reply.IsPending);
        var input = Variables(_handler.Requests[0]).GetProperty("input");
        Assert.Equal("T_1", input.GetProperty("pullRequestReviewThreadId").GetString());
        Assert.Equal("PRR_pending", input.GetProperty("pullRequestReviewId").GetString());
    }

    [Fact]
    public async Task ResolveAndUnresolveUseTheirMutations()
    {
        _handler
            .Respond(HttpStatusCode.OK, """{"data":{"resolveReviewThread":{"thread":{"id":"T_1","isResolved":true}}}}""")
            .Respond(HttpStatusCode.OK, """{"data":{"unresolveReviewThread":{"thread":{"id":"T_1","isResolved":false}}}}""");

        await _client.SetThreadResolvedAsync("T_1", true, CancellationToken.None);
        await _client.SetThreadResolvedAsync("T_1", false, CancellationToken.None);

        Assert.Contains("resolveReviewThread(", Query(_handler.Requests[0]), StringComparison.Ordinal);
        Assert.Contains("unresolveReviewThread(", Query(_handler.Requests[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueCommentPostsToTheIssuesEndpoint()
    {
        _handler.Respond(HttpStatusCode.Created, """
            {"id":11,"node_id":"IC_11","user":{"login":"mona"},"body":"Thanks","created_at":"2026-10-05T00:00:00Z","html_url":"https://github.com/c/11"}
            """);

        var item = await _client.PostIssueCommentAsync(Key, "Thanks", CancellationToken.None);

        Assert.Equal("https://api.github.com/repos/octo/widgets/issues/42/comments", _handler.Requests[0].Uri.ToString());
        Assert.Equal("""{"body":"Thanks"}""", _handler.Requests[0].Body);
        Assert.Equal(("IC_11", "mona", TimelineItemKind.Comment), (item.Id, item.Author, item.Kind));
    }

    [Fact]
    public async Task GraphQlErrorsBecomeExceptions()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":null,"errors":[{"message":"Something broke"}]}""");

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => _client.GetPullRequestDetailsAsync(Key, CancellationToken.None));
        Assert.Contains("Something broke", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestRateLimitHeadersAreTracked()
    {
        _handler.Respond(HttpStatusCode.OK, """{"login":"mona"}""", rateLimit: (42, 1_760_000_000));

        await _client.GetViewerLoginAsync(CancellationToken.None);

        Assert.Equal(new GitHubRateLimit(42, DateTimeOffset.FromUnixTimeSeconds(1_760_000_000)), _client.RateLimit);
    }
}
