using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using Lumen.Contracts;

namespace Lumen.App.Services;

/// <summary>
/// Attaches to a running engine or starts one next to the app (TDD §5.2: the engine is a separate, independently
/// restartable process). Engine output goes to %LOCALAPPDATA%/Lumen/logs/engine.log.
/// </summary>
public sealed class EngineLauncher(string pipeName)
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);

    public string PipeName => pipeName;

    public async Task<(GrpcChannel Channel, int ProcessId)> ConnectAsync(CancellationToken cancellationToken)
    {
        var channel = EngineEndpoint.CreateChannel(pipeName);
        if (await TryPingAsync(channel, cancellationToken).ConfigureAwait(false) is { } pid)
        {
            return (channel, pid);
        }

        using var process = Start();
        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited && await TryPingAsync(channel, cancellationToken).ConfigureAwait(false) is null)
            {
                // Another app instance may have won the race to create the pipe; if not, report the failure.
                throw new EngineUnavailableException($"The review engine exited during startup (code {process.ExitCode}). See {LogPath}.");
            }

            if (await TryPingAsync(channel, cancellationToken).ConfigureAwait(false) is { } started)
            {
                return (channel, started);
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        channel.Dispose();
        throw new EngineUnavailableException($"The review engine did not start within {StartupTimeout.TotalSeconds:0} seconds. See {LogPath}.");
    }

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumen", "logs", "engine.log");

    private static async Task<int?> TryPingAsync(GrpcChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await new ReviewEngine.ReviewEngineClient(channel)
                .PingAsync(new PingRequest(), deadline: DateTime.UtcNow.AddMilliseconds(750), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return reply.ProcessId;
        }
        catch (RpcException)
        {
            return null;
        }
    }

    private Process Start()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "engine");
        var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "Lumen.Engine.exe" : "Lumen.Engine");
        var dll = Path.Combine(directory, "Lumen.Engine.dll");

        ProcessStartInfo info;
        if (File.Exists(executable))
        {
            info = new ProcessStartInfo(executable);
        }
        else if (File.Exists(dll))
        {
            info = new ProcessStartInfo("dotnet");
            info.ArgumentList.Add(dll);
        }
        else
        {
            throw new EngineUnavailableException($"Review engine not found in {directory}.");
        }

        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add("--parent-pid");
        info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.WorkingDirectory = directory;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;

        var process = Process.Start(info) ?? throw new EngineUnavailableException("Could not start the review engine.");
        _ = PumpToLogAsync(process);
        return process;
    }

    private static async Task PumpToLogAsync(Process process)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            await using var log = new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            async Task Pump(StreamReader reader)
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    await log.WriteLineAsync(line).ConfigureAwait(false);
                }
            }

            await Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError)).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Logging is best-effort.
        }
    }
}

public sealed class EngineUnavailableException : Exception
{
    public EngineUnavailableException()
    {
    }

    public EngineUnavailableException(string message)
        : base(message)
    {
    }

    public EngineUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
