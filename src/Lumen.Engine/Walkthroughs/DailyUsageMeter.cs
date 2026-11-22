using System.Text.Json;
using Lumen.Models;

namespace Lumen.Engine.Walkthroughs;

/// <summary>
/// Fast-model requests per local day, kept in <c>{dataDir}/model-usage.json</c> so a restart doesn't reset the cap
/// (Settings → Cloud AI limits).
/// </summary>
public sealed class DailyUsageMeter(string dataDirectory, Func<int> cap, TimeProvider time) : IModelUsageMeter
{
    private readonly Lock _gate = new();
    private string? _day;
    private int _count;

    private string PathOnDisk => Path.Combine(dataDirectory, "model-usage.json");

    public int UsedToday
    {
        get
        {
            lock (_gate)
            {
                Load();
                return _count;
            }
        }
    }

    public bool TryConsume()
    {
        lock (_gate)
        {
            Load();
            if (_count >= cap())
            {
                return false;
            }

            _count++;
            Save();
            return true;
        }
    }

    private string Today => time.GetLocalNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private void Load()
    {
        var today = Today;
        if (_day == today)
        {
            return;
        }

        _day = today;
        _count = 0;
        try
        {
            if (File.Exists(PathOnDisk) && JsonSerializer.Deserialize<Usage>(File.ReadAllText(PathOnDisk)) is { } usage && usage.Day == today)
            {
                _count = usage.Requests;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(new Usage(_day!, _count)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record Usage(string Day, int Requests);
}
