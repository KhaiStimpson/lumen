using System.Security.Cryptography;
using System.Text;
using Lumen.Domain;

namespace Lumen.Repository;

/// <summary>
/// Opens local clones for review, read-only (docs/overhaul.md, Inputs). Committed content is read from the object
/// database (<c>git cat-file</c>, like <c>git show</c>). The working tree is snapshotted into a tree object through a
/// private index (<c>GIT_INDEX_FILE</c> under the data directory) and wrapped in a commit object with fixed dates, so the
/// same uncommitted changes always get the same hash. Nothing checks out, stashes, or writes to the clone's working
/// tree, index or refs: the only writes are new objects in the object database.
/// </summary>
public sealed class GitLocalRepositories(string dataDirectory) : ILocalRepositories
{
    private const string FixedDate = "1970-01-01T00:00:00Z";
    private static readonly string[] DefaultBases = ["origin/HEAD", "main", "master", "develop", "origin/main", "origin/master"];

    private readonly GitRunner git = new();

    public async Task<LocalReview> OpenAsync(LocalBranchTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var root = await TopLevelAsync(target.ClonePath, cancellationToken).ConfigureAwait(false);
        var baseSha = await RevParseCommitAsync(root, target.BaseRef, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"'{target.BaseRef}' isn't a branch or commit in {target.ClonePath}.");

        string headSha;
        string headLabel;
        var uncommitted = false;
        if (target.HeadKind == LocalHeadKind.Branch)
        {
            headSha = await RevParseCommitAsync(root, target.HeadRef!, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException($"'{target.HeadRef}' isn't a branch or commit in {target.ClonePath}.");
            headLabel = target.HeadRef!;
        }
        else
        {
            (headSha, uncommitted) = await SnapshotWorkingTreeAsync(root, target, cancellationToken).ConfigureAwait(false);
            headLabel = await CurrentBranchAsync(root, cancellationToken).ConfigureAwait(false) is { } branch
                ? $"{branch} (working tree)"
                : "working tree";
        }

        var mergeBase = (await git.RunAsync(root, ["merge-base", baseSha, headSha], cancellationToken, throwOnError: false).ConfigureAwait(false))
            .StandardOutput.Trim();
        if (mergeBase.Length == 0)
        {
            throw new ArgumentException($"'{target.BaseRef}' and the head share no history.");
        }

        var author = (await git.RunAsync(root, ["log", "-1", "--format=%an", headSha], cancellationToken, throwOnError: false).ConfigureAwait(false))
            .StandardOutput.Trim();
        return new LocalReview(
            new GitLocalCheckout(git, root, baseSha, headSha, mergeBase),
            target.BaseRef,
            headLabel,
            author.Length > 0 ? author : "you",
            uncommitted);
    }

    public async Task<string> DefaultBaseAsync(string clonePath, CancellationToken cancellationToken)
    {
        var root = await TopLevelAsync(clonePath, cancellationToken).ConfigureAwait(false);
        foreach (var candidate in DefaultBases)
        {
            if (await RevParseCommitAsync(root, candidate, cancellationToken).ConfigureAwait(false) is not null)
            {
                return candidate == "origin/HEAD"
                    ? (await git.RunAsync(root, ["rev-parse", "--abbrev-ref", "origin/HEAD"], cancellationToken, throwOnError: false).ConfigureAwait(false)).StandardOutput.Trim() is { Length: > 0 } name ? name : candidate
                    : candidate;
            }
        }

        throw new ArgumentException($"Couldn't find a base branch (main, master or develop) in {clonePath}; name one.");
    }

    /// <summary>The private index lives under the data directory, one per clone, never inside the clone.</summary>
    public string PrivateIndexPath(LocalBranchTarget target) =>
        Path.Combine(dataDirectory, "local-index", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ClonePath)))[..16] + ".index");

    private async Task<(string Sha, bool Uncommitted)> SnapshotWorkingTreeAsync(string root, LocalBranchTarget target, CancellationToken ct)
    {
        var index = PrivateIndexPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        File.Delete(index);
        var env = new Dictionary<string, string>
        {
            ["GIT_INDEX_FILE"] = index,
            ["GIT_AUTHOR_NAME"] = "Kelpie Review",
            ["GIT_AUTHOR_EMAIL"] = "snapshot@kelpie.invalid",
            ["GIT_COMMITTER_NAME"] = "Kelpie Review",
            ["GIT_COMMITTER_EMAIL"] = "snapshot@kelpie.invalid",
            ["GIT_AUTHOR_DATE"] = FixedDate,
            ["GIT_COMMITTER_DATE"] = FixedDate,

            // Never let git refresh or rewrite the user's real index on our behalf.
            ["GIT_OPTIONAL_LOCKS"] = "0",
        };

        var head = await RevParseCommitAsync(root, "HEAD", ct).ConfigureAwait(false);
        if (head is not null)
        {
            await git.RunAsync(root, ["read-tree", head], ct, env).ConfigureAwait(false);
        }

        // -A: modified, deleted and untracked files, honouring .gitignore. Only the private index and new objects change.
        await git.RunAsync(root, ["-c", "core.fsmonitor=false", "add", "-A", "--", "."], ct, env).ConfigureAwait(false);
        var tree = (await git.RunAsync(root, ["write-tree"], ct, env).ConfigureAwait(false)).StandardOutput.Trim();
        File.Delete(index);

        var headTree = head is null ? null
            : (await git.RunAsync(root, ["rev-parse", head + "^{tree}"], ct).ConfigureAwait(false)).StandardOutput.Trim();
        if (head is not null && tree == headTree)
        {
            return (head, false);
        }

        List<string> args = ["commit-tree", tree, "-m", "Kelpie Review: working tree snapshot"];
        if (head is not null)
        {
            args.InsertRange(2, ["-p", head]);
        }

        var commit = (await git.RunAsync(root, args, ct, env).ConfigureAwait(false)).StandardOutput.Trim();
        return (commit, true);
    }

    private async Task<string> TopLevelAsync(string clonePath, CancellationToken ct)
    {
        var path = clonePath.Replace('/', Path.DirectorySeparatorChar);
        if (!Directory.Exists(path))
        {
            throw new ArgumentException($"'{clonePath}' isn't a folder on this machine.");
        }

        var result = await git.RunAsync(path, ["rev-parse", "--show-toplevel"], ct, throwOnError: false).ConfigureAwait(false);
        return result.Succeeded
            ? Path.GetFullPath(result.StandardOutput.Trim())
            : throw new ArgumentException($"'{clonePath}' isn't a git clone.");
    }

    private async Task<string?> RevParseCommitAsync(string root, string reference, CancellationToken ct)
    {
        if (reference.StartsWith('-'))
        {
            return null;
        }

        var result = await git.RunAsync(root, ["rev-parse", "--verify", "--quiet", reference + "^{commit}"], ct, throwOnError: false).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    private async Task<string?> CurrentBranchAsync(string root, CancellationToken ct)
    {
        var result = await git.RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct, throwOnError: false).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }
}

/// <summary>A local clone at two commits, read entirely from the object database.</summary>
internal sealed class GitLocalCheckout(GitRunner git, string root, string baseSha, string headSha, string mergeBaseSha) : IPullRequestCheckout
{
    public string RootPath { get; } = root;

    public string BaseSha { get; } = baseSha;

    public string HeadSha { get; } = headSha;

    public string MergeBaseSha { get; } = mergeBaseSha;

    public Task<string> GetDiffAsync(CancellationToken cancellationToken) =>
        GetRangeDiffAsync(MergeBaseSha, HeadSha, ignoreWhitespace: false, null, cancellationToken);

    public async Task<string> GetRangeDiffAsync(string fromSha, string toSha, bool ignoreWhitespace, IReadOnlyList<string>? paths, CancellationToken cancellationToken)
    {
        List<string> args = ["diff", "--no-color", "--no-ext-diff", "--no-textconv", "--no-relative", "--src-prefix=a/", "--dst-prefix=b/", "-M", "-U3"];
        if (ignoreWhitespace)
        {
            args.Add("-w");
        }

        args.AddRange([Sha(fromSha), Sha(toSha), "--"]);
        if (paths is not null)
        {
            args.AddRange(paths.Select(Relative));
        }

        return (await git.RunAsync(RootPath, args, cancellationToken).ConfigureAwait(false)).StandardOutput;
    }

    public async Task<bool> IsAncestorAsync(string ancestor, string descendant, CancellationToken cancellationToken) =>
        (await git.RunAsync(RootPath, ["merge-base", "--is-ancestor", Sha(ancestor), Sha(descendant)], cancellationToken, throwOnError: false).ConfigureAwait(false)).Succeeded;

    public async Task<string?> MergeBaseOfAsync(string first, string second, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(RootPath, ["merge-base", Sha(first), Sha(second)], cancellationToken, throwOnError: false).ConfigureAwait(false);
        return result.Succeeded && result.StandardOutput.Trim() is { Length: > 0 } sha ? sha : null;
    }

    /// <summary>A local clone never fetches: a commit is there or it isn't.</summary>
    public async Task<bool> EnsureCommitAsync(string sha, CancellationToken cancellationToken) =>
        (await git.RunAsync(RootPath, ["cat-file", "-e", Sha(sha) + "^{commit}"], cancellationToken, throwOnError: false).ConfigureAwait(false)).Succeeded;

    public Task<string?> ReadBaseFileAsync(string path, CancellationToken cancellationToken) => ReadAtAsync(MergeBaseSha, path, cancellationToken);

    /// <summary>Head content comes from the head commit (or snapshot), never from the working tree on disk.</summary>
    public Task<string?> ReadHeadFileAsync(string path, CancellationToken cancellationToken) => ReadAtAsync(HeadSha, path, cancellationToken);

    private async Task<string?> ReadAtAsync(string commit, string path, CancellationToken ct)
    {
        var gitPath = Relative(path);
        var listing = await git.RunAsync(RootPath, ["ls-tree", "-z", commit, "--", gitPath], ct).ConfigureAwait(false);
        var entry = listing.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', 2))
            .Where(parts => parts.Length == 2 && parts[1] == gitPath)
            .Select(parts => parts[0].Split(' '))
            .FirstOrDefault();
        if (entry is not [_, "blob", var objectId])
        {
            return null;
        }

        return (await git.RunAsync(RootPath, ["cat-file", "blob", objectId], ct).ConfigureAwait(false)).StandardOutput;
    }

    private static string Sha(string sha) =>
        sha is { Length: 40 or 64 } && sha.All(char.IsAsciiHexDigit) ? sha : throw new ArgumentException($"'{sha}' is not a full commit SHA.", nameof(sha));

    private static string Relative(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (normalized.StartsWith('/') || Path.IsPathRooted(path) || normalized.Contains('\0', StringComparison.Ordinal) || segments.Any(s => s is "." or ".."))
        {
            throw new ArgumentException($"Path '{path}' must be relative to the repository root.", nameof(path));
        }

        return string.Join('/', segments);
    }
}
