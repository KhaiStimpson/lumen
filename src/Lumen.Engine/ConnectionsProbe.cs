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
    EngineSettingsStore store,
    string dataDirectory,
    ISecretStore secrets,
    IOpenRouterKeyCheck keyCheck,
    IAgentProvider agent,
    Models.IModelProvider fastModel,
    Models.IModelUsageMeter usage)
{
    public async Task<Connections> GetAsync(CancellationToken cancellationToken)
    {
        var settings = store.Current;
        var jev = JevAsync(settings, cancellationToken);
        var claude = ClaudeAsync(settings, cancellationToken);
        var fast = FastModelAsync(settings, cancellationToken);
        return new Connections
        {
            Jev = await jev.ConfigureAwait(false),
            Claude = await claude.ConfigureAwait(false),
            FastModel = await fast.ConfigureAwait(false),
            SettingsPath = EngineSettings.PathIn(dataDirectory),
            Settings = new ConnectionSettings
            {
                AllowCloudReasoning = settings.Privacy.AllowCloudReasoning,
                JevEnabled = settings.Jev.Enabled,
                AllowCodeSnippetsToJev = settings.Privacy.AllowCodeSnippetsToJev,
                InvestigationsEnabled = settings.Agents.Enabled && settings.Privacy.AllowCodeToAgents,
                JevModel = settings.Jev.Model,
                JevTimeoutSeconds = settings.Jev.TimeoutSeconds,
                JevRequireZeroDataRetention = settings.Jev.RequireZeroDataRetention,
                MaxInvestigationsPerPullRequest = settings.Agents.MaxInvestigationsPerPullRequest,
                MaxConcurrentInvestigations = settings.Agents.MaxConcurrent,
                AllowMeteredUsage = settings.Agents.AllowMeteredUsage,
                AllowCodeToFastModel = settings.Privacy.AllowCodeToFastModel,
                FastModelId = settings.FastModel.Model,
                FastModelTimeoutSeconds = settings.FastModel.TimeoutSeconds,
                FastModelMaxDiffKb = settings.FastModel.MaxDiffKb,
                FastModelDailyRequestCap = settings.FastModel.DailyRequestCap,
                FastModelGateway = settings.FastModel.Gateway,
            },
        };
    }

    private async Task<FastModelConnection> FastModelAsync(EngineSettings settings, CancellationToken cancellationToken)
    {
        var connection = new FastModelConnection
        {
            Model = settings.FastModel.Model,
            CredentialsStored = fastModel.HasCredentials,
            CanStoreCredentials = secrets.IsAvailable,
            Sends = "The pull request's diff, title, description and commit messages (generated files by path and size only)",
            RequestsToday = usage.UsedToday,
        };

        if (!settings.Privacy.AllowCloudReasoning || settings.Privacy.LocalModelsOnly)
        {
            connection.State = ConnectionState.Off;
            connection.Detail = "Off: cloud AI is turned off";
        }
        else if (!fastModel.HasCredentials)
        {
            connection.State = ConnectionState.NotConnected;
            connection.Detail = secrets.IsAvailable ? "No Cloudflare account id and API token" : "No secure credential store on this platform yet";
        }
        else
        {
            var check = await fastModel.CheckAsync(cancellationToken).ConfigureAwait(false);
            connection.State = !check.Valid ? ConnectionState.Error : settings.Privacy.AllowCodeToFastModel ? ConnectionState.Connected : ConnectionState.Off;
            connection.Detail = !check.Valid ? check.Detail : settings.Privacy.AllowCodeToFastModel ? check.Detail : "Connected · walkthrough off until you allow the diff to be sent";
        }

        return connection;
    }

    /// <exception cref="ArgumentException">The account id or token is blank.</exception>
    /// <exception cref="PlatformNotSupportedException">There is no secure credential store on this platform.</exception>
    public void SetCloudflareCredentials(string accountId, string apiToken)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(apiToken))
        {
            throw new ArgumentException("Enter both the Cloudflare account id and an API token.", nameof(apiToken));
        }

        if (!secrets.IsAvailable)
        {
            throw new PlatformNotSupportedException("No secure credential store is available on this platform yet.");
        }

        secrets.Write(SecretNames.CloudflareAccountId, accountId.Trim());
        secrets.Write(SecretNames.CloudflareApiToken, apiToken.Trim());
    }

    public void RemoveCloudflareCredentials()
    {
        secrets.Delete(SecretNames.CloudflareAccountId);
        secrets.Delete(SecretNames.CloudflareApiToken);
    }

    /// <summary>
    /// The switches always change; each Cloud AI limit changes only when it is set, and is kept in a sane range.
    /// Everything else in settings.json is kept.
    /// </summary>
    /// <exception cref="ArgumentException">The JEV model name is blank.</exception>
    public void UpdateSettings(ConnectionSettings changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.HasJevModel && string.IsNullOrWhiteSpace(changes.JevModel))
        {
            throw new ArgumentException("Enter a JEV model, e.g. typesafe/jev-1.13.", nameof(changes));
        }

        if (changes.HasFastModelId && string.IsNullOrWhiteSpace(changes.FastModelId))
        {
            throw new ArgumentException("Enter a Workers AI model, e.g. @cf/zai-org/glm-5.3-flash.", nameof(changes));
        }

        store.Update(current => current with
        {
            Privacy = current.Privacy with
            {
                AllowCloudReasoning = changes.AllowCloudReasoning,
                AllowCodeSnippetsToJev = changes.AllowCodeSnippetsToJev,
                AllowCodeToAgents = changes.InvestigationsEnabled,
                AllowCodeToFastModel = changes.HasAllowCodeToFastModel ? changes.AllowCodeToFastModel : current.Privacy.AllowCodeToFastModel,
            },
            FastModel = current.FastModel with
            {
                Model = changes.HasFastModelId ? changes.FastModelId.Trim() : current.FastModel.Model,
                Gateway = changes.HasFastModelGateway ? changes.FastModelGateway.Trim() : current.FastModel.Gateway,
                TimeoutSeconds = changes.HasFastModelTimeoutSeconds ? Math.Clamp(changes.FastModelTimeoutSeconds, 5, 120) : current.FastModel.TimeoutSeconds,
                MaxDiffKb = changes.HasFastModelMaxDiffKb ? Math.Clamp(changes.FastModelMaxDiffKb, 16, 4096) : current.FastModel.MaxDiffKb,
                DailyRequestCap = changes.HasFastModelDailyRequestCap ? Math.Clamp(changes.FastModelDailyRequestCap, 0, 100_000) : current.FastModel.DailyRequestCap,
            },
            Jev = current.Jev with
            {
                Enabled = changes.JevEnabled,
                Model = changes.HasJevModel ? changes.JevModel.Trim() : current.Jev.Model,
                TimeoutSeconds = changes.HasJevTimeoutSeconds ? Math.Clamp(changes.JevTimeoutSeconds, 2, 60) : current.Jev.TimeoutSeconds,
                RequireZeroDataRetention = changes.HasJevRequireZeroDataRetention ? changes.JevRequireZeroDataRetention : current.Jev.RequireZeroDataRetention,
            },
            Agents = current.Agents with
            {
                Enabled = changes.InvestigationsEnabled,
                MaxInvestigationsPerPullRequest = changes.HasMaxInvestigationsPerPullRequest
                    ? Math.Clamp(changes.MaxInvestigationsPerPullRequest, 0, 20)
                    : current.Agents.MaxInvestigationsPerPullRequest,
                MaxConcurrent = changes.HasMaxConcurrentInvestigations ? Math.Clamp(changes.MaxConcurrentInvestigations, 1, 4) : current.Agents.MaxConcurrent,
                AllowMeteredUsage = changes.HasAllowMeteredUsage ? changes.AllowMeteredUsage : current.Agents.AllowMeteredUsage,
            },
        });
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

    private async Task<JevConnection> JevAsync(EngineSettings settings, CancellationToken cancellationToken)
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
            connection.Detail = settings.Privacy.AllowsJev ? "Turned off" : "Off: cloud AI is turned off";
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

    private async Task<AgentConnection> ClaudeAsync(EngineSettings settings, CancellationToken cancellationToken)
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
                : settings.Privacy.AllowCloudReasoning && !settings.Privacy.LocalModelsOnly ? "Off" : "Off: cloud AI is turned off",
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
