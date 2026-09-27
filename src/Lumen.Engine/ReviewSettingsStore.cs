using System.Text.Json;
using System.Text.RegularExpressions;
using Lumen.Analysis;
using Lumen.Domain;

namespace Lumen.Engine;

/// <summary>
/// One repository's own review settings. A null <see cref="Sensitivity"/> inherits the global one; the lists add to
/// the global lists rather than replacing them.
/// </summary>
public sealed record RepositoryReviewSettings
{
    public ReviewSensitivity? Sensitivity { get; init; }

    public IReadOnlyList<string> IgnoredNames { get; init; } = [];

    public IReadOnlyList<string> MechanicalPaths { get; init; } = [];

    public IReadOnlyList<string> SkippedPaths { get; init; } = [];

    public bool IsEmpty => Sensitivity is null && IgnoredNames.Count == 0 && MechanicalPaths.Count == 0 && SkippedPaths.Count == 0;
}

/// <summary>
/// Review settings (docs/design/settings-overlay.md). The global defaults live in <c>settings.json</c> under
/// <c>review</c>; each repository's overrides in <c>{dataDir}/review/{owner}/{repo}.json</c>. Both stay on this
/// machine. An analysis resolves them once, when it starts.
/// </summary>
public sealed partial class ReviewSettingsStore(EngineSettingsStore engine, string dataDirectory)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Lock _gate = new();

    public ReviewSettings Global => engine.Current.Review;

    public string SettingsPath => EngineSettings.PathIn(dataDirectory);

    /// <exception cref="ArgumentException">The owner or name could not be a GitHub repository.</exception>
    public string PathFor(RepositoryRef repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return Path.Combine(dataDirectory, "review", Segment(repository.Owner), Segment(repository.Name) + ".json");
    }

    /// <summary>Missing or unreadable files mean "no overrides" rather than a failed analysis.</summary>
    public RepositoryReviewSettings Get(RepositoryRef repository)
    {
        var path = PathFor(repository);
        lock (_gate)
        {
            try
            {
                return File.Exists(path)
                    ? Normalise(JsonSerializer.Deserialize<RepositoryReviewSettings>(File.ReadAllText(path), Json) ?? new())
                    : new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new();
            }
        }
    }

    /// <summary>The settings an analysis of <paramref name="repository"/> runs with.</summary>
    public ReviewSettings Resolve(RepositoryRef repository)
    {
        var global = Normalise(Global);
        var own = Get(repository);
        return global with
        {
            Sensitivity = own.Sensitivity ?? global.Sensitivity,
            IgnoredNames = Clean([.. global.IgnoredNames, .. own.IgnoredNames]),
            MechanicalPaths = Clean([.. global.MechanicalPaths, .. own.MechanicalPaths]),
            SkippedPaths = Clean([.. global.SkippedPaths, .. own.SkippedPaths]),
        };
    }

    public void UpdateGlobal(ReviewSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        engine.Update(current => current with { Review = Normalise(settings) });
    }

    /// <summary>Writes the repository's overrides; with none left, removes its file so it inherits everything.</summary>
    public void Update(RepositoryRef repository, RepositoryReviewSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var path = PathFor(repository);
        var normalised = Normalise(settings);
        lock (_gate)
        {
            if (normalised.IsEmpty)
            {
                File.Delete(path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(normalised, Json));
        }
    }

    private static ReviewSettings Normalise(ReviewSettings settings) => settings with
    {
        Sensitivity = (settings.Sensitivity ?? ReviewSensitivity.Balanced).Clamped(),
        IgnoredNames = Clean(settings.IgnoredNames),
        MechanicalPaths = Clean(settings.MechanicalPaths),
        SkippedPaths = Clean(settings.SkippedPaths),
    };

    private static RepositoryReviewSettings Normalise(RepositoryReviewSettings settings) => settings with
    {
        Sensitivity = settings.Sensitivity?.Clamped(),
        IgnoredNames = Clean(settings.IgnoredNames),
        MechanicalPaths = Clean(settings.MechanicalPaths),
        SkippedPaths = Clean(settings.SkippedPaths),
    };

    /// <summary>Trimmed, non-empty, first occurrence kept, in the order they were entered.</summary>
    private static List<string> Clean(IEnumerable<string>? entries) =>
        [.. (entries ?? []).Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.Ordinal)];

    private static string Segment(string value) =>
        SafeSegment().IsMatch(value) && value is not ("." or "..")
            ? value
            : throw new ArgumentException($"'{value}' is not a GitHub owner or repository name.", nameof(value));

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex SafeSegment();
}
