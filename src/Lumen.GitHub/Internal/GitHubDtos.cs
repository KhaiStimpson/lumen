using System.Text.Json.Serialization;

namespace Lumen.GitHub.Internal;

internal sealed record UserDto(string Login);

internal sealed record BranchDto(string Ref, string Sha);

internal sealed record PullRequestDto(
    string Title,
    UserDto? User,
    string State,
    bool? Merged,
    bool? Draft,
    BranchDto Base,
    BranchDto Head,
    string HtmlUrl,
    string? Body,
    DateTimeOffset UpdatedAt);

internal sealed record ReviewCommentDto(
    long Id,
    string Path,
    int? Line,
    int? OriginalLine,
    UserDto? User,
    string Body,
    DateTimeOffset CreatedAt,
    string HtmlUrl);

internal sealed record NewReviewCommentDto(string Body, string CommitId, string Path, int Line, string Side);

internal sealed record PostedCommentDto(long Id, string HtmlUrl);

internal sealed record ErrorDto(string? Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(PullRequestDto))]
[JsonSerializable(typeof(List<ReviewCommentDto>))]
[JsonSerializable(typeof(NewReviewCommentDto))]
[JsonSerializable(typeof(PostedCommentDto))]
[JsonSerializable(typeof(ErrorDto))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;

// GraphQL (camelCase): the only way to read or set a file's "Viewed" checkbox.

internal sealed record GraphQlRequest<TVariables>(string Query, TVariables Variables);

internal sealed record GraphQlResponse<TData>(TData? Data, List<GraphQlErrorDto>? Errors);

internal sealed record GraphQlErrorDto(string Message);

/// <summary><c>After</c> is left out when null: GraphQL rejects a variable the query does not declare.</summary>
internal sealed record PullRequestFilesVariables(
    string Owner,
    string Name,
    int Number,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? After);

internal sealed record FileViewedVariables(string PullRequestId, string Path);

internal sealed record RepositoryData(RepositoryNode? Repository);

internal sealed record RepositoryNode(PullRequestNode? PullRequest);

internal sealed record PullRequestNode(string Id, FileConnection? Files);

internal sealed record FileConnection(List<FileNode> Nodes, PageInfoDto PageInfo);

internal sealed record FileNode(string Path, string ViewerViewedState);

internal sealed record PageInfoDto(bool HasNextPage, string? EndCursor);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GraphQlRequest<PullRequestFilesVariables>))]
[JsonSerializable(typeof(GraphQlRequest<FileViewedVariables>))]
[JsonSerializable(typeof(GraphQlResponse<RepositoryData>))]
[JsonSerializable(typeof(GraphQlResponse<System.Text.Json.JsonElement>))]
internal sealed partial class GitHubGraphQlJsonContext : JsonSerializerContext;
