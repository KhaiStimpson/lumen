using System.Text.Json;
using Lumen.Domain;

namespace Lumen.Analysis.Tests.Support;

/// <summary>Reads the recorded <c>diffs.jsonl</c> replay format (one JSON file per line, proto-JSON diff lines) into changed files.</summary>
internal static class DiffsJsonl
{
    public static IReadOnlyList<ChangedFile> Load(string path)
    {
        var files = new List<ChangedFile>();
        foreach (var line in File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            using var doc = JsonDocument.Parse(line);
            var filePath = doc.RootElement.GetProperty("path").GetString()!;
            var hunks = doc.RootElement.GetProperty("hunks").EnumerateArray().Select(ParseHunk).ToList();
            var additions = hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Added));
            var deletions = hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Removed));
            var isNew = hunks.Count > 0 && hunks.All(h => h.OldCount == 0);
            files.Add(new ChangedFile(
                filePath,
                null,
                isNew ? FileChangeKind.Added : FileChangeKind.Modified,
                additions,
                deletions,
                false,
                MechanicalClassifier.Classify(filePath, hunks),
                hunks));
        }

        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    private static DiffHunk ParseHunk(JsonElement h) => new(
        Int(h, "oldStart"),
        Int(h, "oldCount"),
        Int(h, "newStart"),
        Int(h, "newCount"),
        h.TryGetProperty("header", out var header) ? header.GetString() ?? "" : "",
        h.GetProperty("lines").EnumerateArray().Select(ParseLine).ToList());

    private static DiffLine ParseLine(JsonElement l)
    {
        var kind = l.TryGetProperty("kind", out var k) ? k.GetString() : null;
        var diffKind = kind switch
        {
            "DIFF_LINE_KIND_ADDED" => DiffLineKind.Added,
            "DIFF_LINE_KIND_REMOVED" => DiffLineKind.Removed,
            _ => DiffLineKind.Context,
        };
        return new DiffLine(
            diffKind,
            l.TryGetProperty("oldNumber", out var o) ? o.GetInt32() : null,
            l.TryGetProperty("newNumber", out var n) ? n.GetInt32() : null,
            l.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "");
    }

    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetInt32() : 0;
}
