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
