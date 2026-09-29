using System.Net;
using Lumen.Domain;

namespace Lumen.GitHub.Tests;

public sealed class GitHubRestClientTests : IDisposable
{
    private static readonly PullRequestKey Key = new(new RepositoryRef("octo", "widgets"), 42);

    private readonly FakeHttpMessageHandler _handler = new();
    private readonly HttpClient _http;
    private readonly GitHubRestClient _client;

    public GitHubRestClientTests()
    {
        _http = new HttpClient(_handler);
        _client = new GitHubRestClient(_http, new StaticTokenSource("t0ken"));
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }

    [Fact]
    public async Task GetViewerLoginSendsExpectedUrlAndHeaders()
    {
        _handler.Respond(HttpStatusCode.OK, """{"login":"mona","id":1}""");

        var login = await _client.GetViewerLoginAsync(CancellationToken.None);

        Assert.Equal("mona", login);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/user", request.Uri.ToString());
        Assert.Equal("Bearer t0ken", request.Headers["Authorization"]);
        Assert.Equal("application/vnd.github+json", request.Headers["Accept"]);
        Assert.Equal("2022-11-28", request.Headers["X-GitHub-Api-Version"]);
        Assert.Equal("Lumen", request.Headers["User-Agent"]);
    }

    [Fact]
    public async Task GetPullRequestMapsFields()
    {
        _handler.Respond(HttpStatusCode.OK, PullRequestJson(state: "open", merged: false, draft: true));

        var pr = await _client.GetPullRequestAsync(Key, CancellationToken.None);

        Assert.Equal("https://api.github.com/repos/octo/widgets/pulls/42", _handler.Requests[0].Uri.ToString());
        Assert.Equal(Key, pr.Key);
        Assert.Equal("base111", pr.BaseSha);
        Assert.Equal("head222", pr.HeadSha);
        Assert.Equal(
            new PullRequestMetadata(
                "Add sprockets",
                "hubot",
                "open",
                IsDraft: true,
                "main",
                "feature/sprockets",
                "https://github.com/octo/widgets/pull/42",
                "Body text",
                new DateTimeOffset(2026, 9, 1, 12, 30, 0, TimeSpan.Zero)),
            pr.Metadata);
    }

    [Fact]
    public async Task GetPullRequestReportsMergedState()
    {
        _handler.Respond(HttpStatusCode.OK, PullRequestJson(state: "closed", merged: true, draft: false));

        var pr = await _client.GetPullRequestAsync(Key, CancellationToken.None);

        Assert.Equal("merged", pr.Metadata.State);
        Assert.False(pr.Metadata.IsDraft);
    }

    [Fact]
    public async Task GetReviewCommentsFollowsLinkPagination()
    {
        const string page2 = "https://api.github.com/repositories/1/pulls/42/comments?per_page=100&page=2";
        const string page3 = "https://api.github.com/repositories/1/pulls/42/comments?per_page=100&page=3";
        _handler
            .Respond(HttpStatusCode.OK, $"[{CommentJson(1, line: "10", originalLine: "9")}]",
                $"<{page2}>; rel=\"next\", <{page3}>; rel=\"last\"")
            .Respond(HttpStatusCode.OK, $"[{CommentJson(2, line: "null", originalLine: "7")}]",
                $"<https://api.github.com/x?page=1>; rel=\"prev\", <{page3}>; rel=\"next\"")
            .Respond(HttpStatusCode.OK, $"[{CommentJson(3, line: "null", originalLine: "null")}]",
                $"<{page2}>; rel=\"prev\"");

        var threads = await _client.GetReviewCommentsAsync(Key, CancellationToken.None);

        Assert.Equal(
            [
                "https://api.github.com/repos/octo/widgets/pulls/42/comments?per_page=100",
                page2,
                page3,
            ],
            _handler.Requests.Select(r => r.Uri.ToString()));
        Assert.All(_handler.Requests, r => Assert.Equal("Bearer t0ken", r.Headers["Authorization"]));

        Assert.Equal([1L, 2L, 3L], threads.Select(t => t.Id));
        Assert.Equal([10, 7, null], threads.Select(t => t.Line));
        Assert.Equal(
            new ReviewThread(
                1,
                "src/a.cs",
                10,
                "reviewer",
                "Comment 1",
                new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero),
                "https://github.com/octo/widgets/pull/42#discussion_r1"),
            threads[0]);
    }

    [Fact]
    public async Task PostReviewCommentSendsExactBody()
    {
        _handler.Respond(HttpStatusCode.Created,
            """{"id":987,"html_url":"https://github.com/octo/widgets/pull/42#discussion_r987"}""");

        var posted = await _client.PostReviewCommentAsync(
            new NewReviewComment(Key, "head222", "src/Foo.cs", 17, "Looks off."),
            CancellationToken.None);

        Assert.Equal(new PostedComment(987, "https://github.com/octo/widgets/pull/42#discussion_r987"), posted);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.github.com/repos/octo/widgets/pulls/42/comments", request.Uri.ToString());
        Assert.StartsWith("application/json", request.Headers["Content-Type"], StringComparison.Ordinal);
        Assert.Equal(
            """{"body":"Looks off.","commit_id":"head222","path":"src/Foo.cs","line":17,"side":"RIGHT"}""",
            request.Body);
    }

    [Fact]
    public async Task NonSuccessThrowsWithStatusAndGitHubMessage()
    {
        _handler.Respond(HttpStatusCode.NotFound,
            """{"message":"Not Found","documentation_url":"https://docs.github.com/rest"}""");

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.GetPullRequestAsync(Key, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("Not Found", ex.GitHubMessage);
        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Not Found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnauthorizedSuggestsGhAuthLogin()
    {
        _handler.Respond(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.GetViewerLoginAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal("Bad credentials", ex.GitHubMessage);
        Assert.Contains("gh auth login", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonJsonErrorBodyStillThrowsApiException()
    {
        _handler.Respond(HttpStatusCode.BadGateway, "<html>bad gateway</html>");

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.GetViewerLoginAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.Null(ex.GitHubMessage);
    }

    [Fact]
    public void ConstructorKeepsExistingBaseAddress()
    {
        using var http = new HttpClient(_handler, disposeHandler: false) { BaseAddress = new Uri("https://ghe.example/api/v3/") };

        _ = new GitHubRestClient(http, new StaticTokenSource("x"));

        Assert.Equal(new Uri("https://ghe.example/api/v3/"), http.BaseAddress);
    }

    [Fact]
    public async Task GetViewedFilesPagesThroughGraphQlAndKeepsOnlyViewed()
    {
        _handler
            .Respond(HttpStatusCode.OK, FilesJson(hasNext: true, cursor: "c1", ("src/a.cs", "VIEWED"), ("src/b.cs", "UNVIEWED")))
            .Respond(HttpStatusCode.OK, FilesJson(hasNext: false, cursor: null, ("src/c.cs", "DISMISSED"), ("src/d.cs", "VIEWED")));

        var viewed = await _client.GetViewedFilesAsync(Key, CancellationToken.None);

        Assert.Equal(["src/a.cs", "src/d.cs"], viewed.Order(StringComparer.Ordinal));
        Assert.Equal("https://api.github.com/graphql", _handler.Requests[0].Uri.ToString());
        Assert.Equal(HttpMethod.Post, _handler.Requests[0].Method);
        Assert.Contains("\"owner\":\"octo\"", _handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"number\":42", _handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"after\"", _handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"after\":\"c1\"", _handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "markFileAsViewed")]
    [InlineData(false, "unmarkFileAsViewed")]
    public async Task SetFileViewedLooksUpThePullRequestIdThenMutates(bool viewed, string mutation)
    {
        _handler
            .Respond(HttpStatusCode.OK, """{"data":{"repository":{"pullRequest":{"id":"PR_kw42"}}}}""")
            .Respond(HttpStatusCode.OK, "{\"data\":{\"" + mutation + "\":{\"clientMutationId\":null}}}");

        await _client.SetFileViewedAsync(Key, "src/a.cs", viewed, CancellationToken.None);

        Assert.Equal(2, _handler.Requests.Count);
        Assert.DoesNotContain("$after", _handler.Requests[0].Body, StringComparison.Ordinal);
        var body = _handler.Requests[1].Body!;
        Assert.Contains(mutation + "(", body, StringComparison.Ordinal);
        Assert.Contains("\"pullRequestId\":\"PR_kw42\"", body, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"src/a.cs\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphQlErrorsInA200ResponseThrow()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":null,"errors":[{"message":"Resource not accessible by integration"}]}""");

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => _client.GetViewedFilesAsync(Key, CancellationToken.None));

        Assert.Equal("Resource not accessible by integration", ex.GitHubMessage);
    }

    private static string FilesJson(bool hasNext, string? cursor, params (string Path, string State)[] files)
    {
        var nodes = string.Join(",", files.Select(f => $"{{\"path\":\"{f.Path}\",\"viewerViewedState\":\"{f.State}\"}}"));
        var endCursor = cursor is null ? "null" : $"\"{cursor}\"";
        var pageInfo = $"{{\"hasNextPage\":{(hasNext ? "true" : "false")},\"endCursor\":{endCursor}}}";
        return $"{{\"data\":{{\"repository\":{{\"pullRequest\":{{\"id\":\"PR_kw42\",\"files\":{{\"nodes\":[{nodes}],\"pageInfo\":{pageInfo}}}}}}}}}}}";
    }

    private static string PullRequestJson(string state, bool merged, bool draft) => $$"""
        {
          "number": 42,
          "title": "Add sprockets",
          "user": { "login": "hubot" },
          "state": "{{state}}",
          "merged": {{(merged ? "true" : "false")}},
          "draft": {{(draft ? "true" : "false")}},
          "base": { "ref": "main", "sha": "base111" },
          "head": { "ref": "feature/sprockets", "sha": "head222" },
          "html_url": "https://github.com/octo/widgets/pull/42",
          "body": "Body text",
          "updated_at": "2026-09-01T12:30:00Z"
        }
        """;

    private static string CommentJson(long id, string line, string originalLine) => $$"""
        {
          "id": {{id}},
          "path": "src/a.cs",
          "line": {{line}},
          "original_line": {{originalLine}},
          "user": { "login": "reviewer" },
          "body": "Comment {{id}}",
          "created_at": "2026-09-02T08:00:00Z",
          "html_url": "https://github.com/octo/widgets/pull/42#discussion_r{{id}}"
        }
        """;
}
