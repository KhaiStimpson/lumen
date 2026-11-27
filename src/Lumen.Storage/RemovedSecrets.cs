namespace Lumen.Storage;

/// <summary>The platform credential store (TDD §39A). Today only used to delete credentials for removed services.</summary>
public interface ISecretStore
{
    bool IsAvailable { get; }

    string? Read(string name);

    void Write(string name, string secret);

    bool Delete(string name);
}

/// <summary>
/// Credentials Kelpie Review once stored for services the overhaul removed (OpenRouter for JEV, Cloudflare for the
/// walkthrough). Deleted on engine start so nothing is left behind in Windows Credential Manager.
/// </summary>
public static class RemovedSecrets
{
    public static IReadOnlyList<string> Names { get; } =
    [
        "Lumen/OpenRouter",
        "Lumen/Cloudflare/AccountId",
        "Lumen/Cloudflare/ApiToken",
    ];

    /// <summary>Deletes every removed credential that is still stored; returns how many were. Never throws.</summary>
    public static int DeleteAll(ISecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.IsAvailable)
        {
            return 0;
        }

        var deleted = 0;
        foreach (var name in Names)
        {
            try
            {
                if (store.Delete(name))
                {
                    deleted++;
                }
            }
            catch (Exception)
            {
                // Best effort: a credential that can't be deleted now is tried again next start.
            }
        }

        return deleted;
    }
}
