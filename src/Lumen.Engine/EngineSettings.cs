using System.Text.Json;
using System.Text.Json.Nodes;
using Lumen.Analysis;

namespace Lumen.Engine;

public sealed record InboxPreferences
{
    /// <summary>"owner/name" of repositories whose open pull requests appear in the inbox.</summary>
    public IReadOnlyList<string> WatchedRepositories { get; init; } = [];

    public double PollSeconds { get; init; } = 120;
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

    /// <summary>The keys this version understands, per section. Anything else (the removed AI sections) is dropped.</summary>
    private static readonly Dictionary<string, string[]> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["inbox"] = ["watchedRepositories", "pollSeconds"],
        ["review"] = ["mechanicalPaths", "skippedPaths"],
    };

    public InboxPreferences Inbox { get; init; } = new();

    /// <summary>The global review defaults; repositories override them in their own files (see ReviewSettingsStore).</summary>
    public ReviewSettings Review { get; init; } = new();

    public static string PathIn(string dataDirectory) => System.IO.Path.Combine(dataDirectory, "settings.json");

    /// <summary>Missing or unreadable settings fall back to the safe defaults rather than stopping the engine.</summary>
    public static EngineSettings Load(string dataDirectory)
    {
        var path = PathIn(dataDirectory);
        try
        {
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<EngineSettings>(File.ReadAllText(path), Json) ?? new()).Normalised()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>
    /// Loads the settings and, when the file holds keys this version doesn't know (the removed Cloud AI, JEV, agents
    /// and peer-pattern settings), writes it back without them. Returns the settings either way.
    /// </summary>
    public static EngineSettings LoadAndDropUnknownKeys(string dataDirectory)
    {
        var settings = Load(dataDirectory);
        try
        {
            var path = PathIn(dataDirectory);
            if (File.Exists(path) && HasUnknownKeys(File.ReadAllText(path)))
            {
                settings.Save(dataDirectory);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Best effort: unknown keys are ignored on load anyway.
        }

        return settings;
    }

    /// <summary>True when the JSON has a section or key outside <see cref="KnownKeys"/>.</summary>
    public static bool HasUnknownKeys(string json)
    {
        if (JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is not JsonObject root)
        {
            return false;
        }

        foreach (var (section, value) in root)
        {
            if (!KnownKeys.TryGetValue(section, out var keys))
            {
                return true;
            }

            if (value is JsonObject obj && obj.Any(kv => !keys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Hand-edited JSON can contain null sections or values outside the ranges the settings UI permits.</summary>
    private EngineSettings Normalised()
    {
        var review = Review ?? new ReviewSettings();
        return this with
        {
            Inbox = Inbox ?? new InboxPreferences(),
            Review = review with
            {
                MechanicalPaths = Clean(review.MechanicalPaths),
                SkippedPaths = Clean(review.SkippedPaths),
            },
        };
    }

    private static List<string> Clean(IEnumerable<string>? entries) =>
        [.. (entries ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct(StringComparer.Ordinal)];

    public void Save(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(PathIn(dataDirectory), JsonSerializer.Serialize(this, Json));
    }
}

/// <summary>
/// The live settings. Readers take <see cref="Current"/> at the moment they decide, so a change applies to the next
/// analysis without a restart.
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
