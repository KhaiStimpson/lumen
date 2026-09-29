using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Lumen.Domain;
using Lumen.GitHub.Internal;

namespace Lumen.GitHub;

/// <summary>Minimal GitHub REST v3 client over <see cref="HttpClient"/>.</summary>
public sealed class GitHubRestClient : IGitHubClient
{
    public static readonly Uri DefaultBaseAddress = new("https://api.github.com/");

    private const string ApiVersion = "2022-11-28";
    private const string DeletedUser = "ghost";

    private static readonly MediaTypeWithQualityHeaderValue AcceptHeader = new("application/vnd.github+json");
    private static readonly ProductInfoHeaderValue UserAgentHeader = new("Lumen", null);

    private readonly HttpClient _http;
    private readonly IGitHubTokenSource _tokenSource;

    public GitHubRestClient(HttpClient httpClient, IGitHubTokenSource tokenSource)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(tokenSource);

        _http = httpClient;
        _http.BaseAddress ??= DefaultBaseAddress;
        _tokenSource = tokenSource;
    }

    public async Task<string> GetViewerLoginAsync(CancellationToken cancellationToken)
    {
        var user = await GetAsync(new Uri("user", UriKind.Relative), GitHubJsonContext.Default.UserDto, cancellationToken)
            .ConfigureAwait(false);
        return user.Login;
    }

    public async Task<PullRequestInfo> GetPullRequestAsync(PullRequestKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        var pr = await GetAsync(PullRequestUri(key, string.Empty), GitHubJsonContext.Default.PullRequestDto, cancellationToken)
            .ConfigureAwait(false);

        var metadata = new PullRequestMetadata(
            pr.Title,
            pr.User?.Login ?? DeletedUser,
            pr.Merged == true ? "merged" : pr.State,
            pr.Draft == true,
            pr.Base.Ref,
            pr.Head.Ref,
            pr.HtmlUrl,
            pr.Body,
            pr.UpdatedAt);

        return new PullRequestInfo(key, metadata, pr.Base.Sha, pr.Head.Sha);
    }

    public async Task<IReadOnlyList<ReviewThread>> GetReviewCommentsAsync(PullRequestKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        var threads = new List<ReviewThread>();
        Uri? next = PullRequestUri(key, "/comments?per_page=100");
        while (next is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            var page = await ReadAsync(response, GitHubJsonContext.Default.ListReviewCommentDto, cancellationToken)
                .ConfigureAwait(false);

            threads.AddRange(page.Select(c => new ReviewThread(
                c.Id,
                c.Path,
                c.Line ?? c.OriginalLine,
                c.User?.Login ?? DeletedUser,
                c.Body,
                c.CreatedAt,
                c.HtmlUrl)));

            next = FindNextLink(response);
        }

        return threads;
    }

    public async Task<PostedComment> PostReviewCommentAsync(NewReviewComment comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comment);

        var payload = new NewReviewCommentDto(comment.Body, comment.CommitSha, comment.Path, comment.Line, "RIGHT");
        using var request = new HttpRequestMessage(HttpMethod.Post, PullRequestUri(comment.Key, "/comments"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, GitHubJsonContext.Default.NewReviewCommentDto),
                Encoding.UTF8,
                "application/json"),
        };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var posted = await ReadAsync(response, GitHubJsonContext.Default.PostedCommentDto, cancellationToken)
            .ConfigureAwait(false);

        return new PostedComment(posted.Id, posted.HtmlUrl);
    }

    public async Task<IReadOnlySet<string>> GetViewedFilesAsync(PullRequestKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        var viewed = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        do
        {
            var pullRequest = await GetPullRequestNodeAsync(key, PullRequestFilesQuery, after, cancellationToken).ConfigureAwait(false);
            var files = pullRequest.Files ?? throw new GitHubApiException("GitHub returned no files for the pull request.");
            viewed.UnionWith(files.Nodes.Where(f => f.ViewerViewedState == "VIEWED").Select(f => f.Path));
            after = files.PageInfo.HasNextPage ? files.PageInfo.EndCursor : null;
        }
        while (after is not null);

        return viewed;
    }

    public async Task SetFileViewedAsync(PullRequestKey key, string path, bool viewed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var pullRequest = await GetPullRequestNodeAsync(key, PullRequestIdQuery, null, cancellationToken).ConfigureAwait(false);
        var mutation = viewed ? MarkFileAsViewedMutation : UnmarkFileAsViewedMutation;
        await GraphQlAsync(
            new GraphQlRequest<FileViewedVariables>(mutation, new FileViewedVariables(pullRequest.Id, path)),
            GitHubGraphQlJsonContext.Default.GraphQlRequestFileViewedVariables,
            GitHubGraphQlJsonContext.Default.GraphQlResponseJsonElement,
            cancellationToken).ConfigureAwait(false);
    }

    private const string PullRequestIdQuery = """
        query($owner: String!, $name: String!, $number: Int!) {
          repository(owner: $owner, name: $name) { pullRequest(number: $number) { id } }
        }
        """;

    private const string PullRequestFilesQuery = """
        query($owner: String!, $name: String!, $number: Int!, $after: String) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              id
              files(first: 100, after: $after) {
                nodes { path viewerViewedState }
                pageInfo { hasNextPage endCursor }
              }
            }
          }
        }
        """;

    private const string MarkFileAsViewedMutation = """
        mutation($pullRequestId: ID!, $path: String!) {
          markFileAsViewed(input: { pullRequestId: $pullRequestId, path: $path }) { clientMutationId }
        }
        """;

    private const string UnmarkFileAsViewedMutation = """
        mutation($pullRequestId: ID!, $path: String!) {
          unmarkFileAsViewed(input: { pullRequestId: $pullRequestId, path: $path }) { clientMutationId }
        }
        """;

    private async Task<PullRequestNode> GetPullRequestNodeAsync(PullRequestKey key, string query, string? after, CancellationToken cancellationToken)
    {
        var data = await GraphQlAsync(
            new GraphQlRequest<PullRequestFilesVariables>(
                query,
                new PullRequestFilesVariables(key.Repository.Owner, key.Repository.Name, key.Number, after)),
            GitHubGraphQlJsonContext.Default.GraphQlRequestPullRequestFilesVariables,
            GitHubGraphQlJsonContext.Default.GraphQlResponseRepositoryData,
            cancellationToken).ConfigureAwait(false);
        return data.Repository?.PullRequest
            ?? throw new GitHubApiException(HttpStatusCode.NotFound, $"GitHub GraphQL: pull request {key} was not found.");
    }

    /// <summary>GraphQL reports most failures as 200 with an <c>errors</c> array, so those become exceptions here.</summary>
    private async Task<TData> GraphQlAsync<TVariables, TData>(
        GraphQlRequest<TVariables> body,
        JsonTypeInfo<GraphQlRequest<TVariables>> requestType,
        JsonTypeInfo<GraphQlResponse<TData>> responseType,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("graphql", UriKind.Relative))
        {
            Content = new StringContent(JsonSerializer.Serialize(body, requestType), Encoding.UTF8, "application/json"),
        };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var result = await ReadAsync(response, responseType, cancellationToken).ConfigureAwait(false);
        if (result.Errors is { Count: > 0 } errors)
        {
            var message = string.Join("; ", errors.Select(e => e.Message));
            throw new GitHubApiException(response.StatusCode, $"GitHub GraphQL request failed: {message}", message);
        }

        return result.Data ?? throw new GitHubApiException(response.StatusCode, "GitHub GraphQL returned no data.");
    }

    private static Uri PullRequestUri(PullRequestKey key, string suffix) =>
        new(
            string.Create(
                CultureInfo.InvariantCulture,
                $"repos/{Uri.EscapeDataString(key.Repository.Owner)}/{Uri.EscapeDataString(key.Repository.Name)}/pulls/{key.Number}{suffix}"),
            UriKind.Relative);

    private async Task<T> GetAsync<T>(Uri uri, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokenSource.GetTokenAsync(cancellationToken).ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(AcceptHeader);
        request.Headers.UserAgent.Add(UserAgentHeader);
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw await CreateExceptionAsync(request, response, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false)
                ?? throw new GitHubApiException(response.StatusCode, "GitHub returned an empty response body.");
        }
    }

    private static async Task<GitHubApiException> CreateExceptionAsync(
        HttpRequestMessage request,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string? gitHubMessage = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > 0)
            {
                gitHubMessage = JsonSerializer.Deserialize(body, GitHubJsonContext.Default.ErrorDto)?.Message;
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body (e.g. a proxy page); the status code is enough.
        }

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"GitHub API {request.Method} {request.RequestUri} failed with {(int)response.StatusCode} ({response.ReasonPhrase})");
        if (!string.IsNullOrWhiteSpace(gitHubMessage))
        {
            summary += $": {gitHubMessage}";
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            summary += ". Your GitHub token is missing or expired; run `gh auth login` and try again.";
        }

        return new GitHubApiException(response.StatusCode, summary, gitHubMessage);
    }

    private static Uri? FindNextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        foreach (var link in values.SelectMany(v => v.Split(',')))
        {
            var parts = link.Split(';');
            var target = parts[0].Trim();
            if (target.Length < 2 || target[0] != '<' || target[^1] != '>')
            {
                continue;
            }

            var isNext = parts.Skip(1).Any(p =>
                p.Replace(" ", string.Empty, StringComparison.Ordinal)
                    .Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase));
            if (isNext && Uri.TryCreate(target[1..^1], UriKind.Absolute, out var uri))
            {
                return uri;
            }
        }

        return null;
    }
}
