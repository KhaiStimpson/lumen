using Lumen.Agents.Execution;

namespace Lumen.Agents.Tests;

/// <summary>Replays canned stdout per command; records what would have been run.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, (IReadOnlyList<string> Lines, ProcessResult Result)> _responses = new(StringComparer.Ordinal);

    public bool Installed { get; set; } = true;

    public List<SandboxedCommand> Commands { get; } = [];

    /// <summary>Responds to a command whose arguments start with <paramref name="prefix"/> (space-joined).</summary>
    public FakeProcessRunner On(string prefix, IReadOnlyList<string> lines, ProcessResult? result = null)
    {
        _responses[prefix] = (lines, result ?? new ProcessResult(ProcessEnd.Exited, 0, "", 0));
        return this;
    }

    public string? Resolve(string executable) => Installed ? $"C:/bin/{executable}.exe" : null;

    public async Task<ProcessResult> RunAsync(SandboxedCommand command, Func<string, ValueTask> onOutputLine, CancellationToken cancellationToken)
    {
        Commands.Add(command);
        var joined = string.Join(' ', command.Arguments);
        var match = _responses.Where(r => joined.StartsWith(r.Key, StringComparison.Ordinal)).OrderByDescending(r => r.Key.Length).FirstOrDefault();
        if (match.Key is null)
        {
            return new ProcessResult(ProcessEnd.Exited, 1, "unknown command", 0);
        }

        foreach (var line in match.Value.Lines)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ProcessResult(ProcessEnd.Cancelled, null, "", 0);
            }

            await onOutputLine(line);
        }

        return cancellationToken.IsCancellationRequested ? new ProcessResult(ProcessEnd.Cancelled, null, "", 0) : match.Value.Result;
    }
}
