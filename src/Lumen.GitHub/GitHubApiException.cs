using System.Net;

namespace Lumen.GitHub;

/// <summary>GitHub's REST API returned a non-success status.</summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException()
    {
    }

    public GitHubApiException(string message)
        : base(message)
    {
    }

    public GitHubApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GitHubApiException(HttpStatusCode statusCode, string message, string? gitHubMessage = null)
        : base(message)
    {
        StatusCode = statusCode;
        GitHubMessage = gitHubMessage;
    }

    public HttpStatusCode? StatusCode { get; }

    /// <summary>The <c>message</c> field from GitHub's error payload, when present.</summary>
    public string? GitHubMessage { get; }
}
