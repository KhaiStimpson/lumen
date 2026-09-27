namespace Lumen.Repository;

/// <summary>A git invocation exited with a non-zero status.</summary>
public sealed class GitCommandException : Exception
{
    public GitCommandException()
    {
    }

    public GitCommandException(string message)
        : base(message)
    {
    }

    public GitCommandException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GitCommandException(string command, int exitCode, string standardError)
        : base($"'{command}' failed with exit code {exitCode}: {standardError.Trim()}")
    {
        Command = command;
        ExitCode = exitCode;
        StandardError = standardError;
    }

    public string Command { get; } = string.Empty;

    public int ExitCode { get; }

    public string StandardError { get; } = string.Empty;
}
