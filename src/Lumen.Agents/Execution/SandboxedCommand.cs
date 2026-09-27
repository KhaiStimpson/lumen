namespace Lumen.Agents.Execution;

/// <summary>One agent process: never a shell, always an explicit working directory, timeout and output cap (TDD §41).</summary>
public sealed record SandboxedCommand
{
    /// <summary>A bare executable name such as "claude"; resolved on PATH and checked against the policy.</summary>
    public required string Executable { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>Written to stdin, then stdin is closed. Prompts go here rather than on the command line.</summary>
    public string? StandardInput { get; init; }

    public required TimeSpan Timeout { get; init; }

    /// <summary>Stdout beyond this many bytes stops the process.</summary>
    public long MaxOutputBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Variables set on top of the policy's scrubbed environment.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

public enum ProcessEnd
{
    Exited,
    TimedOut,
    OutputLimitExceeded,
    Cancelled,
}

public sealed record ProcessResult(ProcessEnd End, int? ExitCode, string StandardErrorTail, long OutputBytes)
{
    public bool Succeeded => End == ProcessEnd.Exited && ExitCode == 0;
}

/// <summary>Starts sandboxed processes and streams their stdout line by line. Faked in tests.</summary>
public interface IProcessRunner
{
    /// <summary>Resolves an allowed executable to a full path, or null when it is not installed.</summary>
    string? Resolve(string executable);

    Task<ProcessResult> RunAsync(
        SandboxedCommand command,
        Func<string, ValueTask> onOutputLine,
        CancellationToken cancellationToken);
}

public sealed class CommandRejectedException(string message) : Exception(message);
