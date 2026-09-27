using Lumen.Domain;

namespace Lumen.Repository;

internal sealed class GitPullRequestCheckout : IPullRequestCheckout
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly GitRunner git;
    private readonly string gitDir;
    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, string>?>> environment;

    public GitPullRequestCheckout(
        GitRunner git,
        string gitDir,
        string rootPath,
        string baseSha,
        string headSha,
        string mergeBaseSha,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, string>?>> environment)
    {
        this.git = git;
        this.gitDir = gitDir;
        this.environment = environment;
        RootPath = Path.GetFullPath(rootPath);
        BaseSha = baseSha;
        HeadSha = headSha;
        MergeBaseSha = mergeBaseSha;
    }

    public string RootPath { get; }

    public string BaseSha { get; }

    public string HeadSha { get; }

    public string MergeBaseSha { get; }

    public async Task<string> GetDiffAsync(CancellationToken cancellationToken)
    {
        var env = await environment(cancellationToken).ConfigureAwait(false);
        var result = await git.RunAsync(
            gitDir,
            [
                "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--no-relative",
                "--src-prefix=a/", "--dst-prefix=b/", "-M", "-U3", MergeBaseSha, HeadSha, "--",
            ],
            cancellationToken,
            env).ConfigureAwait(false);
        return result.StandardOutput;
    }

    public async Task<string?> ReadBaseFileAsync(string path, CancellationToken cancellationToken)
    {
        var gitPath = NormalizeRelative(path);

        // ls-tree gives a definitive "absent" (empty output) instead of parsing git show's error text.
        var listing = await git.RunAsync(gitDir, ["ls-tree", "-z", MergeBaseSha, "--", gitPath], cancellationToken)
            .ConfigureAwait(false);
        var entry = listing.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseTreeEntry)
            .FirstOrDefault(e => e.Path == gitPath);
        if (entry.Type != "blob")
        {
            return null;
        }

        var env = await environment(cancellationToken).ConfigureAwait(false);
        var blob = await git.RunAsync(gitDir, ["cat-file", "blob", entry.ObjectId], cancellationToken, env).ConfigureAwait(false);
        return blob.StandardOutput;
    }

    public async Task<string?> ReadHeadFileAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = ResolveUnderRoot(NormalizeRelative(path));
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            return null;
        }

        if (info.LinkTarget is not null)
        {
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null || !target.Exists)
            {
                return null;
            }

            EnsureUnderRoot(target.FullName, path);
        }

        return await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
    }

    private string ResolveUnderRoot(string relative)
    {
        var fullPath = Path.GetFullPath(Path.Combine(RootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        EnsureUnderRoot(fullPath, relative);
        return fullPath;
    }

    private void EnsureUnderRoot(string fullPath, string original)
    {
        var root = RootPath.EndsWith(Path.DirectorySeparatorChar) ? RootPath : RootPath + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, PathComparison))
        {
            throw new ArgumentException($"Path '{original}' resolves outside the checkout.", nameof(original));
        }

        var relative = fullPath[root.Length..];
        if (relative.Equals(".git", PathComparison) || relative.StartsWith(".git" + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new ArgumentException($"Path '{original}' refers to git metadata.", nameof(original));
        }
    }

    private static string NormalizeRelative(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(path) || normalized.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Path '{path}' must be relative to the repository root.", nameof(path));
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is "." or ".."))
        {
            throw new ArgumentException($"Path '{path}' must not contain '.' or '..' segments.", nameof(path));
        }

        return string.Join('/', segments);
    }

    private static (string Type, string ObjectId, string Path) ParseTreeEntry(string line)
    {
        var tab = line.IndexOf('\t', StringComparison.Ordinal);
        if (tab < 0)
        {
            return default;
        }

        var meta = line[..tab].Split(' ');
        return meta.Length == 3 ? (meta[1], meta[2], line[(tab + 1)..]) : default;
    }
}
