using Lumen.Domain;

namespace Lumen.Repository;

public sealed record RepositoryWorkspaceOptions
{
    public string CacheRoot { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumen");

    public Func<RepositoryRef, string> RemoteUrl { get; init; } =
        repo => $"https://github.com/{repo.Owner}/{repo.Name}.git";

    /// <summary>Supplies a GitHub token for fetches; null or empty means anonymous access.</summary>
    public Func<CancellationToken, Task<string?>>? TokenProvider { get; init; }

    public static RepositoryWorkspaceOptions FromTokenSource(IGitHubTokenSource tokenSource)
    {
        ArgumentNullException.ThrowIfNull(tokenSource);
        return new RepositoryWorkspaceOptions
        {
            TokenProvider = async ct => await tokenSource.GetTokenAsync(ct).ConfigureAwait(false),
        };
    }
}
