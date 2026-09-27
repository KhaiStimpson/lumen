namespace Lumen.Agents.Execution;

/// <summary>What agent processes may run, where, and what they inherit (TDD §41).</summary>
public sealed record CommandPolicy
{
    /// <summary>
    /// Enough for a Windows process to start and for a CLI to find its own sign-in. Everything else — API keys,
    /// GitHub tokens, proxies — is dropped unless a command sets it explicitly.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultInheritedVariables =
    [
        "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "windir", "ComSpec", "TEMP", "TMP",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "HOME", "APPDATA", "LOCALAPPDATA", "USERNAME",
        "PROGRAMDATA", "ProgramFiles", "ProgramFiles(x86)", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS",
        "LANG", "TERM", "XDG_CONFIG_HOME", "XDG_DATA_HOME",
    ];

    public required IReadOnlySet<string> AllowedExecutables { get; init; }

    /// <summary>Working directories must be one of these or beneath one.</summary>
    public required IReadOnlyList<string> AllowedWorkingRoots { get; init; }

    public IReadOnlyList<string> InheritedVariables { get; init; } = DefaultInheritedVariables;

    public TimeSpan MaxTimeout { get; init; } = TimeSpan.FromMinutes(15);

    public long MaxOutputBytes { get; init; } = 16 * 1024 * 1024;

    public void Validate(SandboxedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!AllowedExecutables.Contains(command.Executable))
        {
            throw new CommandRejectedException($"'{command.Executable}' is not an allowed agent command.");
        }

        if (command.Timeout <= TimeSpan.Zero || command.Timeout > MaxTimeout)
        {
            throw new CommandRejectedException($"Timeout {command.Timeout} is outside the allowed range (max {MaxTimeout}).");
        }

        if (command.MaxOutputBytes <= 0 || command.MaxOutputBytes > MaxOutputBytes)
        {
            throw new CommandRejectedException($"Output limit {command.MaxOutputBytes} exceeds {MaxOutputBytes} bytes.");
        }

        if (!IsUnderAllowedRoot(command.WorkingDirectory))
        {
            throw new CommandRejectedException($"Working directory '{command.WorkingDirectory}' is outside the agent roots.");
        }
    }

    public bool IsUnderAllowedRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
        {
            return false;
        }

        var full = Normalize(directory);
        return AllowedWorkingRoots.Select(Normalize).Any(root =>
            full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The inherited allowlist from <paramref name="current"/>, then the command's own variables.</summary>
    public Dictionary<string, string> BuildEnvironment(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> overrides)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(overrides);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in InheritedVariables)
        {
            if (current.TryGetValue(name, out var value))
            {
                result[name] = value;
            }
        }

        foreach (var (name, value) in overrides)
        {
            result[name] = value;
        }

        return result;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
