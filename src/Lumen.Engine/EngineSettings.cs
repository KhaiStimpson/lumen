using System.Text.Json;
using Lumen.Domain;

namespace Lumen.Engine;

public sealed record JevSettings
{
    /// <summary>Takes effect only once an OpenRouter key is stored; the key itself lives in the credential store.</summary>
    public bool Enabled { get; init; } = true;

    public string Model { get; init; } = "typesafe/jev-1.13";

    public bool RequireZeroDataRetention { get; init; } = true;

    public double TimeoutSeconds { get; init; } = 12;
}

public sealed record AgentSettings
{
    /// <summary>Off until the user turns it on: investigations send repository code to the agent's provider.</summary>
    public bool Enabled { get; init; }

    public string Provider { get; init; } = "claude-code";

    public int MaxInvestigationsPerPullRequest { get; init; } = 3;

    public int MaxConcurrent { get; init; } = 1;

    /// <summary>Never switch from subscription to metered billing without this explicit opt-in (TDD §37.5).</summary>
    public bool AllowMeteredUsage { get; init; }
}

/// <summary>
/// <c>{dataDir}/settings.json</c>, read at engine start. Holds preferences only — never credentials (TDD §39A).
/// </summary>
public sealed record EngineSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public PrivacySettings Privacy { get; init; } = new();

    public JevSettings Jev { get; init; } = new();

    public AgentSettings Agents { get; init; } = new();

    public bool AgentsAllowed => Agents.Enabled && Privacy.AllowsAgents;

    public static string PathIn(string dataDirectory) => System.IO.Path.Combine(dataDirectory, "settings.json");

    /// <summary>Missing or unreadable settings fall back to the safe defaults rather than stopping the engine.</summary>
    public static EngineSettings Load(string dataDirectory)
    {
        var path = PathIn(dataDirectory);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<EngineSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(PathIn(dataDirectory), JsonSerializer.Serialize(this, Json));
    }
}

/// <summary>
/// The live settings. The Connections panel changes them while the engine runs; readers take <see cref="Current"/>
/// at the moment they decide, so a change applies to the next JEV call or investigation without a restart.
/// </summary>
public sealed class EngineSettingsStore(EngineSettings initial, string dataDirectory)
{
    private readonly Lock _gate = new();

    public EngineSettings Current { get; private set; } = initial;

    /// <summary>Writes the changed settings to disk first, so the file and the engine never disagree.</summary>
    public EngineSettings Update(Func<EngineSettings, EngineSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var next = change(Current);
            next.Save(dataDirectory);
            Current = next;
            return next;
        }
    }
}
