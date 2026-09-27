using System.Text.Json;

namespace Lumen.App.Services;

/// <summary>Small per-user preferences. Never holds credentials (TDD §39A).</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<string> RecentPullRequests { get; set; } = [];

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Null follows the OS setting; true/false overrides it (TDD §25.4).</summary>
    public bool? ReducedMotion { get; set; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumen", "app.json");

    public static AppSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are best-effort.
        }
    }

    public void RememberPullRequest(string reference)
    {
        RecentPullRequests.Remove(reference);
        RecentPullRequests.Insert(0, reference);
        if (RecentPullRequests.Count > 8)
        {
            RecentPullRequests.RemoveRange(8, RecentPullRequests.Count - 8);
        }
    }
}
