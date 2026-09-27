using System.Diagnostics;
using System.Text;

namespace Lumen.Repository;

internal sealed record GitResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs the git CLI without a shell, capturing output asynchronously.</summary>
internal sealed class GitRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string executable;

    public GitRunner(string executable = "git")
    {
        this.executable = executable;
    }

    public async Task<GitResult> RunAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null,
        bool throwOnError = true)
    {
        var args = new List<string> { "-c", "core.quotepath=false" };
        args.AddRange(arguments);

        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "never";
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var timeoutSource = new CancellationTokenSource(timeout ?? DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using var process = new Process { StartInfo = startInfo };

        process.Start();
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"'{Describe(args)}' timed out after {timeout ?? DefaultTimeout}.");
        }

        var result = new GitResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));

        if (throwOnError && !result.Succeeded)
        {
            throw new GitCommandException(Describe(args), result.ExitCode, result.StandardError);
        }

        return result;
    }

    private string Describe(IEnumerable<string> args) =>
        executable + " " + string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
