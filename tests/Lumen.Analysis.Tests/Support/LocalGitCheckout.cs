using System.Diagnostics;
using Lumen.Domain;

namespace Lumen.Analysis.Tests.Support;

/// <summary>A checkout backed by an existing local clone whose working tree is at <see cref="HeadSha"/>.</summary>
internal sealed class LocalGitCheckout(string root, string baseSha, string headSha, string mergeBaseSha) : IPullRequestCheckout
{
    public string RootPath => root;

    public string BaseSha => baseSha;

    public string HeadSha => headSha;

    public string MergeBaseSha => mergeBaseSha;

    public Task<string> GetDiffAsync(CancellationToken cancellationToken) =>
        Git(cancellationToken, "diff", "--no-color", "--no-ext-diff", "-M", "-U3", mergeBaseSha, headSha)!;

    public async Task<string?> ReadBaseFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await Git(cancellationToken, "show", $"{mergeBaseSha}:{path}");
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<string?> ReadHeadFileAsync(string path, CancellationToken cancellationToken)
    {
        var full = Path.Combine(root, path);
        return File.Exists(full) ? await File.ReadAllTextAsync(full, cancellationToken) : null;
    }

    public static async Task<string> RevParse(string root, string rev) =>
        (await new LocalGitCheckout(root, "", "", "").Git(CancellationToken.None, "rev-parse", rev)).Trim();

    private async Task<string> Git(CancellationToken cancellationToken, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0 ? await stdout : throw new InvalidOperationException(await stderr);
    }
}
