namespace Lumen.Domain;

public sealed record RepositoryRef(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";

    public override string ToString() => FullName;
}

public sealed record PullRequestKey(RepositoryRef Repository, int Number)
{
    public override string ToString() => $"{Repository.FullName}#{Number}";

    /// <summary>Parses "owner/repo#123", "owner/repo/pull/123" or a github.com pull request URL.</summary>
    public static bool TryParse(string? text, out PullRequestKey key)
    {
        key = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            value = uri.AbsolutePath.Trim('/');
        }

        string owner, name, number;
        var hash = value.IndexOf('#', StringComparison.Ordinal);
        if (hash > 0)
        {
            var repo = value[..hash].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (repo.Length != 2)
            {
                return false;
            }

            (owner, name, number) = (repo[0], repo[1], value[(hash + 1)..]);
        }
        else
        {
            var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || !parts[2].Equals("pull", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            (owner, name, number) = (parts[0], parts[1], parts[3]);
        }

        if (!int.TryParse(number, out var n) || n <= 0)
        {
            return false;
        }

        key = new PullRequestKey(new RepositoryRef(owner, name), n);
        return true;
    }
}

public sealed record PullRequestMetadata(
    string Title,
    string Author,
    string State,
    bool IsDraft,
    string BaseRef,
    string HeadRef,
    string Url,
    string? Body,
    DateTimeOffset UpdatedAt);

/// <summary>Immutable view of a pull request at a specific head commit (TDD §6.1).</summary>
public sealed record PullRequestSnapshot(
    PullRequestKey Key,
    string BaseSha,
    string HeadSha,
    string MergeBaseSha,
    PullRequestMetadata Metadata,
    IReadOnlyList<ChangedFile> Files,
    IReadOnlyList<ReviewThread> Threads)
{
    public ChangedFile? FindFile(string path) =>
        Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal));
}

public sealed record ReviewThread(
    long Id,
    string Path,
    int? Line,
    string Author,
    string Body,
    DateTimeOffset CreatedAt,
    string Url);
