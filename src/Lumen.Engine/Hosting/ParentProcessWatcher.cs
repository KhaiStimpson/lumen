using System.Diagnostics;

namespace Lumen.Engine.Hosting;

/// <summary>Stops the engine when the desktop client that launched it exits, so no orphan daemons linger.</summary>
public sealed partial class ParentProcessWatcher(
    int parentProcessId,
    IHostApplicationLifetime lifetime,
    ILogger<ParentProcessWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            await parent.WaitForExitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The parent is already gone.
        }
        catch (OperationCanceledException)
        {
            return;
        }

        LogParentExited(logger, parentProcessId);
        lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Parent process {ProcessId} exited; shutting down")]
    private static partial void LogParentExited(ILogger logger, int processId);
}
