using Lumen.Agents;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Jev;

namespace Lumen.Engine;

/// <summary>
/// Connection status for the app's Connections panel and <c>Lumen.Engine connections</c> (TDD §39). Checking spends
/// no usage: the OpenRouter key-info endpoint and Claude Code's sign-in status are both free. Keys are written to
/// the credential store and never read back out of it here.
/// </summary>
public sealed class ConnectionsProbe(
    EngineSettings settings,
    string dataDirectory,
    ISecretStore secrets,
    IOpenRouterKeyCheck keyCheck,
    IAgentProvider agent)
{
    public async Task<Connections> GetAsync(CancellationToken cancellationToken)
    {
        var jev = JevAsync(cancellationToken);
        var claude = ClaudeAsync(cancellationToken);
        return new Connections
        {
            Jev = await jev.ConfigureAwait(false),
            Claude = await claude.ConfigureAwait(false),
            SettingsPath = EngineSettings.PathIn(dataDirectory),
        };
    }

    /// <exception cref="ArgumentException">The key is blank.</exception>
    /// <exception cref="PlatformNotSupportedException">There is no secure credential store on this platform.</exception>
    public void SetOpenRouterKey(string key)
    {
        var trimmed = key?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("No key entered.", nameof(key));
        }

        if (!secrets.IsAvailable)
        {
            throw new PlatformNotSupportedException("No secure credential store is available on this platform yet.");
        }

        secrets.Write(SecretNames.OpenRouterApiKey, trimmed);
    }

    public bool RemoveOpenRouterKey() => secrets.Delete(SecretNames.OpenRouterApiKey);

    private async Task<JevConnection> JevAsync(CancellationToken cancellationToken)
    {
        var stored = HasKey();
        var connection = new JevConnection
        {
            Model = settings.Jev.Model,
            KeyStored = stored,
            CanStoreKey = secrets.IsAvailable,
            Sends = settings.Privacy.AllowCodeSnippetsToJev
                ? "Counts, categories and convention wording"
                : "Counts and categories only — no code, paths or names",
        };

        if (!settings.Jev.Enabled || !settings.Privacy.AllowsJev)
        {
            connection.State = ConnectionState.Off;
            connection.Detail = "Off in settings (jev.enabled / privacy.allowCloudReasoning)";
        }
        else if (!stored)
        {
            connection.State = ConnectionState.NotConnected;
            connection.Detail = secrets.IsAvailable ? "No OpenRouter key" : "No secure credential store on this platform yet";
        }
        else
        {
            var check = await keyCheck.CheckKeyAsync(cancellationToken).ConfigureAwait(false);
            connection.State = check.Valid ? ConnectionState.Connected : ConnectionState.Error;
            connection.Detail = check.Detail;
        }

        return connection;
    }

    private async Task<AgentConnection> ClaudeAsync(CancellationToken cancellationToken)
    {
        var auth = await agent.GetAuthenticationStateAsync(cancellationToken).ConfigureAwait(false);
        var agents = settings.Agents;
        return new AgentConnection
        {
            State = auth.IsUsable ? ConnectionState.Connected : ConnectionState.NotConnected,
            Detail = auth.Detail,
            Version = auth.Version ?? "",
            Billing = auth.Billing switch
            {
                AgentBilling.Subscription => ConnectionBilling.Subscription,
                AgentBilling.Metered => ConnectionBilling.Metered,
                _ => ConnectionBilling.Unknown,
            },
            InvestigationsEnabled = settings.AgentsAllowed,
            InvestigationsDetail = settings.AgentsAllowed
                ? $"On · up to {agents.MaxInvestigationsPerPullRequest} per pull request · {(agents.AllowMeteredUsage ? "metered usage allowed" : "subscription only")}"
                : "Off (settings: agents.enabled and privacy.allowCodeToAgents must both be true)",
        };
    }

    private bool HasKey()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(secrets.Read(SecretNames.OpenRouterApiKey));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
