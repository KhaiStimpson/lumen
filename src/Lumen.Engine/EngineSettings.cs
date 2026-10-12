using System.Text.Json;
using Lumen.Analysis;
using Lumen.Domain;

namespace Lumen.Engine;

public sealed record JevSettings
{
    /// <summary>
    /// Legacy (plan v2): JEV is kept but off by default, and only takes part in analysis when this is on at engine
    /// start and an OpenRouter key is stored. The rule-based policy is the floor either way.
    /// </summary>
    public bool Enabled { get; init; }

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

public sealed record InboxPreferences
{
    /// <summary>"owner/name" of repositories whose open pull requests appear in the inbox.</summary>
    public IReadOnlyList<string> WatchedRepositories { get; init; } = [];

    public double PollSeconds { get; init; } = 120;
}

/// <summary>The fast model (plan v2 M7): GLM on Cloudflare Workers AI. Credentials live in the credential store.</summary>
public sealed record FastModelSettings
{
    /// <summary>The Workers AI model id. GLM 5.3 Flash; confirm the exact name in the Workers AI catalog.</summary>
    public string Model { get; init; } = "@cf/zai-org/glm-5.3-flash";

    /// <summary>Optional AI Gateway id; empty calls Workers AI directly.</summary>
    public string Gateway { get; init; } = "";

    public double TimeoutSeconds { get; init; } = 30;

    /// <summary>Largest diff sent per pull request, in KB. Larger files are cut and the rest summarised by path and size.</summary>
    public int MaxDiffKb { get; init; } = 512;

    public int DailyRequestCap { get; init; } = 500;

    /// <summary>Files summarised at once.</summary>
    public int MaxConcurrent { get; init; } = 6;
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

    public InboxPreferences Inbox { get; init; } = new();

    public FastModelSettings FastModel { get; init; } = new();

    /// <summary>The global review defaults; repositories override them in their own files (see ReviewSettingsStore).</summary>
    public ReviewSettings Review { get; init; } = new();

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
