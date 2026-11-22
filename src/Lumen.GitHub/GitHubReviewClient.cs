using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lumen.Domain;
using Lumen.GitHub.Internal;

namespace Lumen.GitHub;

/// <summary>
/// The review client's GitHub surface (plan v2): inbox search, pull request details, review threads, pending reviews.
/// Review data is GraphQL (threads, pending reviews and search have no REST equivalent worth using); comments posted
/// at once and issue comments stay on REST, where they're simpler.
/// </summary>
public sealed partial class GitHubRestClient
{
    private readonly Lock _rateGate = new();
    private readonly Dictionary<PullRequestKey, string> _pullRequestIds = [];
    private GitHubRateLimit? _rateLimit;

    public GitHubRateLimit? RateLimit
    {
        get
        {
            lock (_rateGate)
            {
                return _rateLimit;
            }
        }
    }

    // ---------------------------------------------------------------- Inbox

    private const string InboxFields = """
        fragment InboxPullRequest on PullRequest {
          number title url isDraft createdAt updatedAt additions deletions changedFiles headRefOid reviewDecision
          author { login }
          repository { name owner { login } }
          commits(last: 1) { nodes { commit { statusCheckRollup { state } } } }
        }
        """;

    public async Task<InboxSearchResult> SearchInboxAsync(IReadOnlyList<InboxQuery> queries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queries);

        var query = new StringBuilder("query(");
        query.Append(string.Join(", ", queries.Select((_, i) => string.Create(CultureInfo.InvariantCulture, $"$q{i}: String!"))));
        if (queries.Count == 0)
        {
            query.Clear().Append("query {");
        }
        else
        {
            query.Append(") {");
        }

        query.Append(" viewer { login } rateLimit { remaining resetAt }");
        var variables = new JsonObject();
        for (var i = 0; i < queries.Count; i++)
        {
            query.Append(CultureInfo.InvariantCulture, $" s{i}: search(query: $q{i}, type: ISSUE, first: 50) {{ nodes {{ ...InboxPullRequest }} }}");
            variables[string.Create(CultureInfo.InvariantCulture, $"q{i}")] = queries[i].Search;
        }

        query.Append(" }\n").Append(queries.Count == 0 ? "" : InboxFields);
        var data = await GraphQlNodeAsync(query.ToString(), variables, cancellationToken).ConfigureAwait(false);

        var results = new Dictionary<string, IReadOnlyList<InboxItem>>(StringComparer.Ordinal);
        for (var i = 0; i < queries.Count; i++)
        {
            var nodes = data[string.Create(CultureInfo.InvariantCulture, $"s{i}")]?["nodes"]?.AsArray() ?? [];
            results[queries[i].Id] = [.. nodes.OfType<JsonObject>().Where(n => n["number"] is not null).Select(ToInboxItem)];
        }

        return new InboxSearchResult(Str(data["viewer"]?["login"]), results);
    }

    private static InboxItem ToInboxItem(JsonObject node) => new()
    {
        Key = new PullRequestKey(
            new RepositoryRef(Str(node["repository"]?["owner"]?["login"]), Str(node["repository"]?["name"])),
            Int(node["number"])),
        Title = Str(node["title"]),
        Author = Login(node["author"]),
        CreatedAt = Time(node["createdAt"]) ?? default,
        UpdatedAt = Time(node["updatedAt"]) ?? default,
        Additions = Int(node["additions"]),
        Deletions = Int(node["deletions"]),
        ChangedFiles = Int(node["changedFiles"]),
        IsDraft = Bool(node["isDraft"]),
        Url = Str(node["url"]),
        HeadSha = Str(node["headRefOid"]),
        Review = Str(node["reviewDecision"]) switch
        {
            "APPROVED" => InboxReviewState.Approved,
            "CHANGES_REQUESTED" => InboxReviewState.ChangesRequested,
            "REVIEW_REQUIRED" => InboxReviewState.Awaiting,
            _ => InboxReviewState.None,
        },
        Checks = Str(node["commits"]?["nodes"]?[0]?["commit"]?["statusCheckRollup"]?["state"]) switch
        {
            "SUCCESS" => InboxCheckState.Success,
            "FAILURE" or "ERROR" => InboxCheckState.Failure,
            "PENDING" or "EXPECTED" => InboxCheckState.Pending,
            _ => InboxCheckState.None,
        },
    };

    // ---------------------------------------------------------------- Details

    private const string DetailsQuery = """
        query($owner: String!, $name: String!, $number: Int!) {
          viewer { login }
          rateLimit { remaining resetAt }
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              id title body url state isDraft merged createdAt mergeable mergeStateStatus
              baseRefName headRefName headRefOid
              author { login }
              labels(first: 30) { nodes { name color } }
              reviewRequests(first: 30) {
                nodes { requestedReviewer { __typename ... on User { login } ... on Bot { login } ... on Mannequin { login } ... on Team { slug organization { login } } } }
              }
              latestOpinionatedReviews(first: 30) { nodes { author { login } state commit { oid } } }
              timelineItems(last: 100, itemTypes: [ISSUE_COMMENT, PULL_REQUEST_REVIEW]) {
                nodes {
                  __typename
                  ... on IssueComment { id author { login } body createdAt url }
                  ... on PullRequestReview { id author { login } body state createdAt submittedAt url commit { oid } comments { totalCount } }
                }
              }
              commits(last: 100) {
                nodes { commit { oid messageHeadline message authoredDate author { name user { login } } } }
              }
              head: commits(last: 1) {
                nodes {
                  commit {
                    statusCheckRollup {
                      contexts(first: 100) {
                        nodes {
                          __typename
                          ... on CheckRun { name status conclusion startedAt completedAt detailsUrl }
                          ... on StatusContext { context state targetUrl createdAt description }
                        }
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    public async Task<PullRequestDetails> GetPullRequestDetailsAsync(PullRequestKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var data = await GraphQlNodeAsync(DetailsQuery, PullRequestVariables(key), cancellationToken).ConfigureAwait(false);
        var pr = data["repository"]?["pullRequest"] as JsonObject
            ?? throw new GitHubApiException(HttpStatusCode.NotFound, $"GitHub GraphQL: pull request {key} was not found.");
        Remember(key, Str(pr["id"]));

        var viewer = Str(data["viewer"]?["login"]);
        var author = Login(pr["author"]);
        var timeline = Nodes(pr["timelineItems"])
            .Select(ToTimelineItem)
            .OfType<TimelineItem>()
            .Where(t => t.Kind == TimelineItemKind.Comment || t.Verdict != ReviewVerdict.Pending)
            .OrderBy(t => t.CreatedAt)
            .ToList();

        var reviewers = new List<PullRequestReviewer>();
        foreach (var node in Nodes(pr["latestOpinionatedReviews"]))
        {
            reviewers.Add(new PullRequestReviewer(Login(node["author"]), false, false, Verdict(Str(node["state"])), Str(node["commit"]?["oid"])));
        }

        foreach (var node in Nodes(pr["reviewRequests"]))
        {
            var reviewer = node["requestedReviewer"];
            var isTeam = Str(reviewer?["__typename"]) == "Team";
            var login = isTeam ? $"{Str(reviewer?["organization"]?["login"])}/{Str(reviewer?["slug"])}" : Str(reviewer?["login"]);
            if (login.Length == 0)
            {
                continue;
            }

            var existing = reviewers.FindIndex(r => r.Login.Equals(login, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                reviewers[existing] = reviewers[existing] with { IsRequested = true };
            }
            else
            {
                reviewers.Add(new PullRequestReviewer(login, isTeam, true, null));
            }
        }

        // People who only commented still reviewed; show them after the verdicts.
        foreach (var review in timeline.Where(t => t.Kind == TimelineItemKind.Review && t.Verdict == ReviewVerdict.Commented))
        {
            if (!review.Author.Equals(author, StringComparison.OrdinalIgnoreCase)
                && !reviewers.Any(r => r.Login.Equals(review.Author, StringComparison.OrdinalIgnoreCase)))
            {
                reviewers.Add(new PullRequestReviewer(review.Author, false, false, ReviewVerdict.Commented, review.CommitSha));
            }
        }

        var viewerReviewed = timeline
            .Where(t => t.Kind == TimelineItemKind.Review && t.Author.Equals(viewer, StringComparison.OrdinalIgnoreCase) && t.CommitSha is { Length: > 0 })
            .Select(t => t.CommitSha)
            .LastOrDefault();

        return new PullRequestDetails
        {
            Key = key,
            Title = Str(pr["title"]),
            Author = author,
            Body = Str(pr["body"]),
            State = Bool(pr["merged"]) ? "merged" : Str(pr["state"]).ToLowerInvariant(),
            IsDraft = Bool(pr["isDraft"]),
            BaseRef = Str(pr["baseRefName"]),
            HeadRef = Str(pr["headRefName"]),
            HeadSha = Str(pr["headRefOid"]),
            Url = Str(pr["url"]),
            CreatedAt = Time(pr["createdAt"]) ?? default,
            Mergeability = Str(pr["mergeable"]) switch
            {
                "MERGEABLE" => Mergeability.Mergeable,
                "CONFLICTING" => Mergeability.Conflicting,
                _ => Mergeability.Unknown,
            },
            MergeState = Str(pr["mergeStateStatus"]),
            Labels = [.. Nodes(pr["labels"]).Select(n => new PullRequestLabel(Str(n["name"]), Str(n["color"])))],
            Reviewers = reviewers,
            Timeline = timeline,
            Commits = [.. Nodes(pr["commits"]).Select(n => n["commit"]).OfType<JsonObject>().Select(c => new CommitInfo(
                Str(c["oid"]),
                Str(c["messageHeadline"]),
                Str(c["message"]),
                Login(c["author"]?["user"]) is { Length: > 0 } user && user != DeletedUser ? user : Str(c["author"]?["name"]),
                Time(c["authoredDate"]) ?? default))],
            Checks = [.. Nodes(pr["head"]?["nodes"]?[0]?["commit"]?["statusCheckRollup"]?["contexts"]).Select(ToCheck)],
            ViewerLogin = viewer,
            ViewerLastReviewedSha = viewerReviewed,
        };
    }

    private static TimelineItem? ToTimelineItem(JsonObject node)
    {
        var type = Str(node["__typename"]);
        if (type == "IssueComment")
        {
            return new TimelineItem
            {
                Id = Str(node["id"]),
                Kind = TimelineItemKind.Comment,
                Author = Login(node["author"]),
                Body = Str(node["body"]),
                CreatedAt = Time(node["createdAt"]) ?? default,
                Url = Str(node["url"]),
            };
        }

        if (type == "PullRequestReview")
        {
            return new TimelineItem
            {
                Id = Str(node["id"]),
                Kind = TimelineItemKind.Review,
                Author = Login(node["author"]),
                Body = Str(node["body"]),
                CreatedAt = Time(node["submittedAt"]) ?? Time(node["createdAt"]) ?? default,
                Url = Str(node["url"]),
                Verdict = Verdict(Str(node["state"])),
                ReviewCommentCount = Int(node["comments"]?["totalCount"]),
                CommitSha = Str(node["commit"]?["oid"]) is { Length: > 0 } sha ? sha : null,
            };
        }

        return null;
    }

    private static CheckRunInfo ToCheck(JsonObject node)
    {
        if (Str(node["__typename"]) == "StatusContext")
        {
            var state = Str(node["state"]);
            return new CheckRunInfo(
                Str(node["context"]),
                state switch
                {
                    "SUCCESS" => CheckState.Success,
                    "FAILURE" or "ERROR" => CheckState.Failure,
                    _ => CheckState.Pending,
                },
                Str(node["description"]) is { Length: > 0 } description ? description : state,
                Time(node["createdAt"]),
                null,
                Str(node["targetUrl"]),
                IsStatus: true);
        }

        var status = Str(node["status"]);
        var conclusion = Str(node["conclusion"]);
        var checkState = status != "COMPLETED" ? CheckState.Pending : conclusion switch
        {
            "SUCCESS" => CheckState.Success,
            "NEUTRAL" => CheckState.Neutral,
            "SKIPPED" => CheckState.Skipped,
            "CANCELLED" or "STALE" => CheckState.Cancelled,
            _ => CheckState.Failure,
        };
        return new CheckRunInfo(
            Str(node["name"]),
            checkState,
            status != "COMPLETED" ? status : conclusion,
            Time(node["startedAt"]),
            Time(node["completedAt"]),
            Str(node["detailsUrl"]),
            IsStatus: false);
    }

    private static ReviewVerdict Verdict(string state) => state switch
    {
        "APPROVED" => ReviewVerdict.Approved,
        "CHANGES_REQUESTED" => ReviewVerdict.ChangesRequested,
        "DISMISSED" => ReviewVerdict.Dismissed,
        "PENDING" => ReviewVerdict.Pending,
        _ => ReviewVerdict.Commented,
    };

    // ---------------------------------------------------------------- Threads and the pending review

    private const string ThreadsQuery = """
        query($owner: String!, $name: String!, $number: Int!, $after: String) {
          viewer { login }
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              id
              reviews(states: [PENDING], first: 5) {
                nodes {
                  id body viewerDidAuthor
                  comments(first: 100) { nodes { id body path line startLine originalLine originalStartLine replyTo { id } } }
                }
              }
              reviewThreads(first: 100, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  id path diffSide startDiffSide line startLine originalLine originalStartLine isResolved isOutdated
                  viewerCanReply viewerCanResolve viewerCanUnresolve
                  comments(first: 100) {
                    nodes { id databaseId author { login } body createdAt url diffHunk originalCommit { oid } pullRequestReview { id state } }
                  }
                }
              }
            }
          }
        }
        """;

    public async Task<ReviewThreadsResult> GetReviewThreadsAsync(PullRequestKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        var threads = new List<ReviewThread>();
        JsonObject? pendingNode = null;
        string? after = null;
        do
        {
            var variables = PullRequestVariables(key);
            if (after is not null)
            {
                variables["after"] = after;
            }

            var data = await GraphQlNodeAsync(ThreadsQuery, variables, cancellationToken).ConfigureAwait(false);
            var pr = data["repository"]?["pullRequest"] as JsonObject
                ?? throw new GitHubApiException(HttpStatusCode.NotFound, $"GitHub GraphQL: pull request {key} was not found.");
            Remember(key, Str(pr["id"]));
            pendingNode ??= Nodes(pr["reviews"]).FirstOrDefault(r => Bool(r["viewerDidAuthor"]));
            threads.AddRange(Nodes(pr["reviewThreads"]).Select(ToThread));

            var page = pr["reviewThreads"]?["pageInfo"];
            after = Bool(page?["hasNextPage"]) ? Str(page?["endCursor"]) : null;
        }
        while (after is not null);

        PendingReview? pending = null;
        if (pendingNode is not null)
        {
            var reviewId = Str(pendingNode["id"]);
            var comments = new List<PendingComment>();
            foreach (var comment in Nodes(pendingNode["comments"]))
            {
                var id = Str(comment["id"]);
                var thread = threads.FirstOrDefault(t => t.Comments.Any(c => c.Id == id));
                comments.Add(new PendingComment
                {
                    Id = id,
                    ThreadId = thread?.Id,
                    Path = Str(comment["path"]),
                    Side = thread?.Side ?? DiffSide.Right,
                    Line = IntOrNull(comment["line"]) ?? IntOrNull(comment["originalLine"]) ?? thread?.Line ?? 0,
                    StartLine = IntOrNull(comment["startLine"]) ?? IntOrNull(comment["originalStartLine"]),
                    Body = Str(comment["body"]),
                    IsReply = comment["replyTo"] is JsonObject,
                });
            }

            pending = new PendingReview { Id = reviewId, Body = Str(pendingNode["body"]), Comments = comments };
        }

        return new ReviewThreadsResult(threads, pending);
    }

    private static ReviewThread ToThread(JsonObject node)
    {
        var comments = Nodes(node["comments"]).Select(c => new ThreadComment
        {
            Id = Str(c["id"]),
            DatabaseId = c["databaseId"] is JsonValue v && v.TryGetValue<long>(out var databaseId) ? databaseId : 0,
            Author = Login(c["author"]),
            Body = Str(c["body"]),
            CreatedAt = Time(c["createdAt"]) ?? default,
            Url = Str(c["url"]),
            ReviewId = Str(c["pullRequestReview"]?["id"]) is { Length: > 0 } review ? review : null,
            IsPending = Str(c["pullRequestReview"]?["state"]) == "PENDING",
        }).ToList();
        var first = Nodes(node["comments"]).FirstOrDefault();

        return new ReviewThread
        {
            Id = Str(node["id"]),
            Path = Str(node["path"]),
            Side = Str(node["diffSide"]) == "LEFT" ? DiffSide.Left : DiffSide.Right,
            Line = IntOrNull(node["line"]),
            StartLine = IntOrNull(node["startLine"]),
            OriginalLine = IntOrNull(node["originalLine"]),
            OriginalStartLine = IntOrNull(node["originalStartLine"]),
            OriginalCommitSha = first is null ? null : Str(first["originalCommit"]?["oid"]),
            IsResolved = Bool(node["isResolved"]),
            IsOutdated = Bool(node["isOutdated"]),
            CanReply = Bool(node["viewerCanReply"]),
            CanResolve = Bool(node["viewerCanResolve"]),
            CanUnresolve = Bool(node["viewerCanUnresolve"]),
            DiffHunk = first is null ? null : Str(first["diffHunk"]),
            Comments = comments,
        };
    }

    // ---------------------------------------------------------------- Writes

    public async Task<TimelineItem> PostIssueCommentAsync(PullRequestKey key, string body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var uri = new Uri(
            string.Create(
                CultureInfo.InvariantCulture,
                $"repos/{Uri.EscapeDataString(key.Repository.Owner)}/{Uri.EscapeDataString(key.Repository.Name)}/issues/{key.Number}/comments"),
            UriKind.Relative);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new NewIssueCommentDto(body), GitHubJsonContext.Default.NewIssueCommentDto),
                Encoding.UTF8,
                "application/json"),
        };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var posted = await ReadAsync(response, GitHubJsonContext.Default.IssueCommentDto, cancellationToken).ConfigureAwait(false);
        return new TimelineItem
        {
            Id = posted.NodeId,
            Kind = TimelineItemKind.Comment,
            Author = posted.User?.Login ?? DeletedUser,
            Body = posted.Body,
            CreatedAt = posted.CreatedAt,
            Url = posted.HtmlUrl,
        };
    }

    public async Task<ThreadComment> ReplyToThreadAsync(string threadId, string body, string? pendingReviewId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var input = new JsonObject { ["pullRequestReviewThreadId"] = threadId, ["body"] = body };
        if (pendingReviewId is not null)
        {
            input["pullRequestReviewId"] = pendingReviewId;
        }

        var data = await GraphQlNodeAsync(
            """
            mutation($input: AddPullRequestReviewThreadReplyInput!) {
              addPullRequestReviewThreadReply(input: $input) {
                comment { id databaseId author { login } body createdAt url pullRequestReview { id state } }
              }
            }
            """,
            new JsonObject { ["input"] = input },
            cancellationToken).ConfigureAwait(false);
        var comment = data["addPullRequestReviewThreadReply"]?["comment"]
            ?? throw new GitHubApiException("GitHub returned no comment for the reply.");
        return new ThreadComment
        {
            Id = Str(comment["id"]),
            DatabaseId = comment["databaseId"] is JsonValue v && v.TryGetValue<long>(out var id) ? id : 0,
            Author = Login(comment["author"]),
            Body = Str(comment["body"]),
            CreatedAt = Time(comment["createdAt"]) ?? DateTimeOffset.UtcNow,
            Url = Str(comment["url"]),
            ReviewId = Str(comment["pullRequestReview"]?["id"]),
            IsPending = Str(comment["pullRequestReview"]?["state"]) == "PENDING",
        };
    }

    public Task SetThreadResolvedAsync(string threadId, bool resolved, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        var mutation = resolved
            ? "mutation($threadId: ID!) { resolveReviewThread(input: { threadId: $threadId }) { thread { id isResolved } } }"
            : "mutation($threadId: ID!) { unresolveReviewThread(input: { threadId: $threadId }) { thread { id isResolved } } }";
        return GraphQlNodeAsync(mutation, new JsonObject { ["threadId"] = threadId }, cancellationToken);
    }

    public async Task<string> StartPendingReviewAsync(PullRequestKey key, string commitSha, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var pullRequestId = await PullRequestIdAsync(key, cancellationToken).ConfigureAwait(false);
        var data = await GraphQlNodeAsync(
            """
            mutation($pullRequestId: ID!, $commitOID: GitObjectID) {
              addPullRequestReview(input: { pullRequestId: $pullRequestId, commitOID: $commitOID }) { pullRequestReview { id } }
            }
            """,
            new JsonObject { ["pullRequestId"] = pullRequestId, ["commitOID"] = commitSha },
            cancellationToken).ConfigureAwait(false);
        return Str(data["addPullRequestReview"]?["pullRequestReview"]?["id"]) is { Length: > 0 } id
            ? id
            : throw new GitHubApiException("GitHub did not start a review.");
    }

    public async Task<string> AddPendingThreadAsync(string reviewId, NewReviewComment comment, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reviewId);
        ArgumentNullException.ThrowIfNull(comment);

        var input = new JsonObject
        {
            ["pullRequestReviewId"] = reviewId,
            ["path"] = comment.Path,
            ["line"] = comment.Line,
            ["side"] = SideName(comment.Side),
            ["body"] = comment.Body,
        };
        if (comment.StartLine is { } start && start != comment.Line)
        {
            input["startLine"] = start;
            input["startSide"] = SideName(comment.StartSide ?? comment.Side);
        }

        var data = await GraphQlNodeAsync(
            """
            mutation($input: AddPullRequestReviewThreadInput!) {
              addPullRequestReviewThread(input: $input) { thread { id comments(first: 1) { nodes { id } } } }
            }
            """,
            new JsonObject { ["input"] = input },
            cancellationToken).ConfigureAwait(false);
        return Str(data["addPullRequestReviewThread"]?["thread"]?["comments"]?["nodes"]?[0]?["id"]) is { Length: > 0 } id
            ? id
            : throw new GitHubApiException("GitHub did not add the comment to the review (is the line part of the diff?).");
    }

    public Task UpdatePendingCommentAsync(string commentId, string body, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(commentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        return GraphQlNodeAsync(
            """
            mutation($id: ID!, $body: String!) {
              updatePullRequestReviewComment(input: { pullRequestReviewCommentId: $id, body: $body }) { pullRequestReviewComment { id } }
            }
            """,
            new JsonObject { ["id"] = commentId, ["body"] = body },
            cancellationToken);
    }

    public Task DeletePendingCommentAsync(string commentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(commentId);
        return GraphQlNodeAsync(
            "mutation($id: ID!) { deletePullRequestReviewComment(input: { id: $id }) { pullRequestReview { id } } }",
            new JsonObject { ["id"] = commentId },
            cancellationToken);
    }

    /// <summary>Discards a pending review and its comments (nothing was ever visible to others).</summary>
    public Task DeletePendingReviewAsync(string reviewId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reviewId);
        return GraphQlNodeAsync(
            "mutation($id: ID!) { deletePullRequestReview(input: { pullRequestReviewId: $id }) { pullRequestReview { id } } }",
            new JsonObject { ["id"] = reviewId },
            cancellationToken);
    }

    public async Task<SubmittedReview> SubmitReviewAsync(string reviewId, ReviewEvent reviewEvent, string body, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reviewId);
        var input = new JsonObject
        {
            ["pullRequestReviewId"] = reviewId,
            ["event"] = reviewEvent switch
            {
                ReviewEvent.Approve => "APPROVE",
                ReviewEvent.RequestChanges => "REQUEST_CHANGES",
                _ => "COMMENT",
            },
        };
        if (!string.IsNullOrWhiteSpace(body))
        {
            input["body"] = body;
        }

        var data = await GraphQlNodeAsync(
            """
            mutation($input: SubmitPullRequestReviewInput!) {
              submitPullRequestReview(input: $input) { pullRequestReview { id url } }
            }
            """,
            new JsonObject { ["input"] = input },
            cancellationToken).ConfigureAwait(false);
        var review = data["submitPullRequestReview"]?["pullRequestReview"];
        return new SubmittedReview(Str(review?["id"]), Str(review?["url"]));
    }

    // ---------------------------------------------------------------- Plumbing

    private async Task<string> PullRequestIdAsync(PullRequestKey key, CancellationToken cancellationToken)
    {
        lock (_rateGate)
        {
            if (_pullRequestIds.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var node = await GetPullRequestNodeAsync(key, PullRequestIdQuery, null, cancellationToken).ConfigureAwait(false);
        Remember(key, node.Id);
        return node.Id;
    }

    private void Remember(PullRequestKey key, string id)
    {
        if (id.Length == 0)
        {
            return;
        }

        lock (_rateGate)
        {
            _pullRequestIds[key] = id;
        }
    }

    private static JsonObject PullRequestVariables(PullRequestKey key) => new()
    {
        ["owner"] = key.Repository.Owner,
        ["name"] = key.Repository.Name,
        ["number"] = key.Number,
    };

    /// <summary>A GraphQL request whose result is read as a tree; errors become exceptions.</summary>
    private async Task<JsonObject> GraphQlNodeAsync(string query, JsonObject variables, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["query"] = query, ["variables"] = variables };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("graphql", UriKind.Relative))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException("GitHub GraphQL returned something that isn't JSON.", ex);
        }

        if (root?["errors"] is JsonArray { Count: > 0 } errors)
        {
            var message = string.Join("; ", errors.Select(e => Str(e?["message"])));
            if (message.Contains("pending review", StringComparison.OrdinalIgnoreCase) && message.Contains("one", StringComparison.OrdinalIgnoreCase))
            {
                throw new PendingReviewExistsException(message);
            }

            throw new GitHubApiException(response.StatusCode, $"GitHub GraphQL request failed: {message}", message);
        }

        var data = root?["data"] as JsonObject ?? throw new GitHubApiException(response.StatusCode, "GitHub GraphQL returned no data.");
        if (data["rateLimit"] is JsonObject rate && Time(rate["resetAt"]) is { } reset)
        {
            lock (_rateGate)
            {
                _rateLimit = new GitHubRateLimit(Int(rate["remaining"]), reset);
            }
        }

        return data;
    }

    private void TrackRateLimit(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && int.TryParse(remaining.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var left)
            && response.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            lock (_rateGate)
            {
                _rateLimit = new GitHubRateLimit(left, DateTimeOffset.FromUnixTimeSeconds(epoch));
            }
        }
    }

    private static string SideName(DiffSide side) => side == DiffSide.Left ? "LEFT" : "RIGHT";

    private static IEnumerable<JsonObject> Nodes(JsonNode? connection) =>
        (connection is JsonArray array ? array : connection?["nodes"] as JsonArray)?.OfType<JsonObject>() ?? [];

    private static string Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static string Login(JsonNode? actor) => Str(actor?["login"]) is { Length: > 0 } login ? login : DeletedUser;

    private static int Int(JsonNode? node) => IntOrNull(node) ?? 0;

    private static int? IntOrNull(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static bool Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static DateTimeOffset? Time(JsonNode? node) =>
        DateTimeOffset.TryParse(Str(node), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;
}
