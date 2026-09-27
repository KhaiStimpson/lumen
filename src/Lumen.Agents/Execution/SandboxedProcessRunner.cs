using System.Collections;
using System.Diagnostics;
using System.Text;

namespace Lumen.Agents.Execution;

/// <summary>
/// Runs allowlisted executables without a shell: explicit working directory, scrubbed environment, prompt on stdin,
/// timeout, stdout cap and process-tree kill (TDD §41).
/// </summary>
public sealed class SandboxedProcessRunner(CommandPolicy policy) : IProcessRunner
{
    private const int StandardErrorTailChars = 4000;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public string? Resolve(string executable)
    {
        if (!policy.AllowedExecutables.Contains(executable))
        {
            return null;
        }

        var names = OperatingSystem.IsWindows() ? [executable + ".exe"] : new[] { executable };
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }

        return null;
    }

    public async Task<ProcessResult> RunAsync(
        SandboxedCommand command,
        Func<string, ValueTask> onOutputLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onOutputLine);
        policy.Validate(command);
        var executable = Resolve(command.Executable)
            ?? throw new CommandRejectedException($"'{command.Executable}' is not installed or not on PATH.");

        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var (name, value) in policy.BuildEnvironment(CurrentEnvironment(), command.Environment))
        {
            startInfo.Environment[name] = value;
        }

        using var timeout = new CancellationTokenSource(command.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stderr = new StringBuilder();
        var stderrTask = PumpStandardErrorAsync(process.StandardError, stderr);

        long bytes = 0;
        var end = ProcessEnd.Exited;
        try
        {
            if (command.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(command.StandardInput.AsMemory(), linked.Token).ConfigureAwait(false);
            }

            process.StandardInput.Close();

            while (await process.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false) is { } line)
            {
                bytes += Utf8.GetByteCount(line) + 1;
                if (bytes > command.MaxOutputBytes)
                {
                    end = ProcessEnd.OutputLimitExceeded;
                    break;
                }

                await onOutputLine(line).ConfigureAwait(false);
            }

            if (end == ProcessEnd.Exited)
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            end = cancellationToken.IsCancellationRequested ? ProcessEnd.Cancelled : ProcessEnd.TimedOut;
        }
        catch (IOException) when (process.HasExited)
        {
        }

        if (end != ProcessEnd.Exited)
        {
            Kill(process);
        }

        await stderrTask.ConfigureAwait(false);
        var tail = stderr.Length > StandardErrorTailChars ? stderr.ToString(stderr.Length - StandardErrorTailChars, StandardErrorTailChars) : stderr.ToString();
        return new ProcessResult(end, end == ProcessEnd.Exited ? process.ExitCode : null, tail, bytes);
    }

    private static async Task PumpStandardErrorAsync(StreamReader reader, StringBuilder buffer)
    {
        try
        {
            var chunk = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(chunk.AsMemory()).ConfigureAwait(false)) > 0)
            {
                buffer.Append(chunk, 0, read);
                if (buffer.Length > StandardErrorTailChars * 4)
                {
                    buffer.Remove(0, buffer.Length - StandardErrorTailChars);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static Dictionary<string, string> CurrentEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
