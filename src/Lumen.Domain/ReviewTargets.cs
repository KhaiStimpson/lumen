namespace Lumen.Domain;

/// <summary>What the head of a local review is: a branch (committed content) or the working tree, uncommitted changes included.</summary>
public enum LocalHeadKind
{
    Branch,
    WorkingTree,
}

/// <summary>
/// What is being reviewed: a GitHub pull request, or a local clone's branch or working tree against a base ref.
/// Storage keys on <see cref="Key"/> instead of a pull request number.
/// </summary>
public abstract record ReviewTarget
{
    /// <summary>A stable string identity, used as the storage key ("pr:owner/repo#12", "local:c:/src/app|main|WorkingTree").</summary>
    public abstract string Key { get; }

    /// <summary>For example "owner/repo#12" or "app · feature/x vs main".</summary>
    public abstract string DisplayName { get; }

    /// <summary>Posting (comments, reviews, Viewed ticks) only exists for pull requests.</summary>
    public abstract bool CanPost { get; }

    /// <summary>Parses a <see cref="Key"/> back; false for anything unrecognised.</summary>
    public static bool TryParse(string? key, out ReviewTarget target)
    {
        target = null!;
        if (key is null)
        {
            return false;
        }

        if (key.StartsWith("pr:", StringComparison.Ordinal) && PullRequestKey.TryParse(key[3..], out var pr))
        {
            target = new PullRequestTarget(pr);
            return true;
        }

        if (key.StartsWith("local:", StringComparison.Ordinal))
        {
            var parts = key[6..].Split('|');
            if (parts.Length == 3 && parts[2].Split(':', 2) is [var kind, ..] split && Enum.TryParse<LocalHeadKind>(kind, out var headKind))
            {
                target = new LocalBranchTarget(parts[0], parts[1], headKind, split.Length > 1 ? split[1] : null);
                return true;
            }
        }

        return false;
    }
}

public sealed record PullRequestTarget(PullRequestKey PullRequest) : ReviewTarget
{
    public override string Key => "pr:" + PullRequest;

    public override string DisplayName => PullRequest.ToString();

    public override bool CanPost => true;
}

/// <summary>
/// A local clone reviewed read-only: <see cref="BaseRef"/> against a branch (<see cref="HeadRef"/>) or the working tree.
/// </summary>
public sealed record LocalBranchTarget : ReviewTarget
{
    public LocalBranchTarget(string clonePath, string baseRef, LocalHeadKind headKind, string? headRef = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clonePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);
        if (headKind == LocalHeadKind.Branch)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(headRef);
        }

        ClonePath = Normalise(clonePath);
        BaseRef = baseRef.Trim();
        HeadKind = headKind;
        HeadRef = headKind == LocalHeadKind.Branch ? headRef!.Trim() : null;
    }

    public string ClonePath { get; }

    public string BaseRef { get; }

    public LocalHeadKind HeadKind { get; }

    /// <summary>The branch for <see cref="LocalHeadKind.Branch"/>; null for the working tree.</summary>
    public string? HeadRef { get; }

    public string FolderName => ClonePath.TrimEnd('/').Split('/')[^1];

    public override string Key => $"local:{ClonePath}|{BaseRef}|{HeadKind}" + (HeadRef is null ? "" : ":" + HeadRef);

    public override string DisplayName => $"{FolderName} · {HeadRef ?? "working tree"} vs {BaseRef}";

    public override bool CanPost => false;

    /// <summary>
    /// The pull-request-shaped key local sessions carry through code written for pull requests: owner "local", the
    /// folder name plus a hash of <see cref="Key"/> (two targets on one clone stay apart), number 0. Never sent to
    /// GitHub (<see cref="CanPost"/> is false).
    /// </summary>
    public PullRequestKey SyntheticKey => new(new RepositoryRef("local", $"{SafeName(FolderName)}-{ShortHash(Key)}"), 0);

    private static string SafeName(string name)
    {
        var safe = new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-')]).Trim('-', '.');
        return safe.Length == 0 ? "clone" : safe.Length > 60 ? safe[..60] : safe;
    }

    private static string ShortHash(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();

    /// <summary>Forward slashes, no trailing slash, lower-case drive letter: one clone, one key.</summary>
    public static string Normalise(string path)
    {
        var full = path.Trim().Replace('\\', '/').TrimEnd('/');
        return full.Length >= 2 && full[1] == ':' ? char.ToLowerInvariant(full[0]) + full[1..] : full;
    }
}

/// <summary>A local clone opened for review: the checkout at base and head, and what the snapshot shows about it.</summary>
public sealed record LocalReview(IPullRequestCheckout Checkout, string BaseRef, string HeadLabel, string Author, bool HasUncommittedChanges);

/// <summary>Opens local clones read-only (docs/overhaul.md, Inputs).</summary>
public interface ILocalRepositories
{
    /// <summary>Resolves base and head (snapshotting the working tree when asked) without touching the clone's files or index.</summary>
    Task<LocalReview> OpenAsync(LocalBranchTarget target, CancellationToken cancellationToken);

    /// <summary>The clone's default base: origin/HEAD, else main, master or develop.</summary>
    Task<string> DefaultBaseAsync(string clonePath, CancellationToken cancellationToken);
}
