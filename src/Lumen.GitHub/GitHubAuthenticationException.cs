namespace Lumen.GitHub;

/// <summary>No usable GitHub token could be obtained.</summary>
public sealed class GitHubAuthenticationException : Exception
{
    public GitHubAuthenticationException()
        : this("GitHub authentication is not configured. Run `gh auth login` and try again.")
    {
    }

    public GitHubAuthenticationException(string message)
        : base(message)
    {
    }

    public GitHubAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
