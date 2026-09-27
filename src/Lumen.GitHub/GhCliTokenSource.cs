using System.ComponentModel;
using System.Diagnostics;
using Lumen.Domain;

namespace Lumen.GitHub;

/// <summary>
/// Resolves a token from <c>LUMEN_GITHUB_TOKEN</c>, falling back to <c>gh auth token</c>. The token is cached for the process lifetime.
/// </summary>
public sealed class GhCliTokenSource : IGitHubTokenSource, IDisposable
{
    public const string EnvironmentVariableName = "LUMEN_GITHUB_TOKEN";

    private const string LoginHint = "Run `gh auth login` (GitHub CLI) and try again.";
    private static readonly TimeSpan GhTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly string _ghExecutable;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;

    public GhCliTokenSource()
        : this(Environment.GetEnvironmentVariable, "gh")
    {
    }

    internal GhCliTokenSource(Func<string, string?> getEnvironmentVariable, string ghExecutable)
    {
        _getEnvironmentVariable = getEnvironmentVariable;
        _ghExecutable = ghExecutable;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is null)
            {
                var fromEnvironment = _getEnvironmentVariable(EnvironmentVariableName)?.Trim();
                _token = string.IsNullOrEmpty(fromEnvironment)
                    ? await ReadFromGhAsync(cancellationToken).ConfigureAwait(false)
                    : fromEnvironment;
            }

            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<string> ReadFromGhAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_ghExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("auth");
        startInfo.ArgumentList.Add("token");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new GitHubAuthenticationException(
                $"GitHub CLI (`gh`) was not found and {EnvironmentVariableName} is not set. Install the GitHub CLI, then {LoginHint}",
                ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GhTimeout);

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new GitHubAuthenticationException($"`gh auth token` did not respond within {GhTimeout.TotalSeconds:0}s. {LoginHint}");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var token = (await stdout.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0 || token.Length == 0)
        {
            var detail = (await stderr.ConfigureAwait(false)).Trim();
            throw new GitHubAuthenticationException(
                detail.Length == 0
                    ? $"`gh auth token` returned no token. {LoginHint}"
                    : $"`gh auth token` failed: {detail}. {LoginHint}");
        }

        return token;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
