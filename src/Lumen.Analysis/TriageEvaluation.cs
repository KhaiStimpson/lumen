using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumen.Domain;

namespace Lumen.Analysis;

/// <summary>
/// One human judgement about a span of a pull request: "this is where a reviewer should (not) look".
/// Lines are 1-based and inclusive, on the head side unless <see cref="Side"/> is "old" (pure deletions have no head lines).
/// </summary>
public sealed record TriageLabel
{
    public required string Path { get; init; }

    public required int StartLine { get; init; }

    public required int EndLine { get; init; }

    public required TriageTier Tier { get; init; }

    public string Note { get; init; } = "";

    /// <summary>"head" (default) or "old".</summary>
    public string Side { get; init; } = "head";

    /// <summary>True until a human has confirmed the label.</summary>
    public bool Draft { get; init; }
}

/// <summary>The contents of <c>tests/fixtures/triage/&lt;owner&gt;-&lt;repo&gt;-&lt;pr&gt;/labels.json</c>.</summary>
public sealed record TriageLabelSet(IReadOnlyList<TriageLabel> Labels)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TriageLabelSet Parse(string json) =>
        JsonSerializer.Deserialize<TriageLabelSet>(json, Json) ?? new TriageLabelSet([]);

    public static TriageLabelSet Load(string path) => Parse(File.ReadAllText(path));

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>A label that expects real behaviour, overlapping a hunk triage called mechanical and Skip. The worst bug triage can ship.</summary>
public sealed record MechanicalFalsePositive(TriageLabel Label, HunkTriage Hunk);

public sealed record TriageEvaluationReport(
    int CriticalLabels,
    IReadOnlyList<TriageLabel> MissedCritical,
    IReadOnlyList<MechanicalFalsePositive> MechanicalFalsePositives,
    IReadOnlyList<TriageLabel> UnmatchedLabels,
    IReadOnlyDictionary<TriageTier, int> LinesPerTier,
    IReadOnlyDictionary<ChangeClass, int> LinesPerClass,
    int DraftLabels)
{
    public int CriticalRecalled => CriticalLabels - MissedCritical.Count;

    /// <summary>Share of Critical labels that landed in Critical or Worth a look; 1 when there are none.</summary>
    public double CriticalRecall => CriticalLabels == 0 ? 1 : (double)CriticalRecalled / CriticalLabels;

    public int TotalLines => LinesPerTier.Values.Sum();

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("Critical recall: ").Append(CriticalRecalled).Append('/').Append(CriticalLabels)
            .Append(" (").Append(CriticalRecall.ToString("P0", System.Globalization.CultureInfo.InvariantCulture)).AppendLine(")");
        foreach (var miss in MissedCritical)
        {
            sb.Append("  missed: ").Append(Where(miss)).Append(' ').AppendLine(miss.Note);
        }

        sb.Append("Mechanical false positives: ").Append(MechanicalFalsePositives.Count).AppendLine();
        foreach (var fp in MechanicalFalsePositives)
        {
            sb.Append("  ").Append(Where(fp.Label)).Append(" labelled ").Append(fp.Label.Tier)
                .Append(" but triaged ").Append(fp.Hunk.Class).Append('/').Append(fp.Hunk.Tier).AppendLine();
        }

        if (UnmatchedLabels.Count > 0)
        {
            sb.Append("Labels matching no hunk: ").AppendLine(string.Join(", ", UnmatchedLabels.Select(Where)));
        }

        if (DraftLabels > 0)
        {
            sb.Append("Draft labels (not yet confirmed by a human): ").Append(DraftLabels).AppendLine();
        }

        sb.Append("Lines per tier (").Append(TotalLines).AppendLine(" changed):");
        foreach (var tier in Enum.GetValues<TriageTier>())
        {
            sb.Append("  ").Append(tier).Append(": ").AppendLine(LinesPerTier.GetValueOrDefault(tier).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        sb.AppendLine("Lines per class:");
        foreach (var (cls, lines) in LinesPerClass.OrderByDescending(kv => kv.Value))
        {
            sb.Append("  ").Append(cls).Append(": ").AppendLine(lines.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static string Where(TriageLabel l) => $"{l.Path}:{l.StartLine}-{l.EndLine}{(l.Side == "old" ? " (old)" : "")}";
}

/// <summary>Scores a triage against human labels: did critical hunks surface, and was anything real called mechanical?</summary>
public static class TriageEvaluation
{
    private static readonly HashSet<ChangeClass> MechanicalClasses =
    [
        ChangeClass.Formatting, ChangeClass.CommentsOnly, ChangeClass.ImportsOnly,
        ChangeClass.Rename, ChangeClass.Move, ChangeClass.Ripple, ChangeClass.Generated,
    ];

    public static TriageEvaluationReport Evaluate(TriageLabelSet labels, TriageResult triage)
    {
        var missed = new List<TriageLabel>();
        var falsePositives = new List<MechanicalFalsePositive>();
        var unmatched = new List<TriageLabel>();
        var critical = 0;

        foreach (var label in labels.Labels)
        {
            var overlapping = triage.Hunks.Where(h => Overlaps(label, h)).ToList();
            if (overlapping.Count == 0)
            {
                unmatched.Add(label);
            }

            if (label.Tier == TriageTier.Critical)
            {
                critical++;
                if (!overlapping.Any(h => h.Tier is TriageTier.Critical or TriageTier.WorthALook))
                {
                    missed.Add(label);
                }
            }

            if (label.Tier is TriageTier.Critical or TriageTier.WorthALook)
            {
                falsePositives.AddRange(overlapping
                    .Where(h => h.Tier == TriageTier.Skip && MechanicalClasses.Contains(h.Class))
                    .Select(h => new MechanicalFalsePositive(label, h)));
            }
        }

        return new TriageEvaluationReport(
            critical,
            missed,
            falsePositives,
            unmatched,
            SumBy(triage.Hunks, h => h.Tier),
            SumBy(triage.Hunks, h => h.Class),
            labels.Labels.Count(l => l.Draft));
    }

    private static Dictionary<TKey, int> SumBy<TKey>(IEnumerable<HunkTriage> hunks, Func<HunkTriage, TKey> key)
        where TKey : notnull =>
        hunks.GroupBy(key).ToDictionary(g => g.Key, g => g.Sum(h => h.ChangedLines));

    private static bool Overlaps(TriageLabel label, HunkTriage hunk)
    {
        if (!string.Equals(label.Path, hunk.Path, StringComparison.Ordinal))
        {
            return false;
        }

        var (start, end) = string.Equals(label.Side, "old", StringComparison.OrdinalIgnoreCase)
            ? (hunk.OldStart, hunk.OldEnd)
            : (hunk.NewStart, hunk.NewEnd);

        return start is { } s && end is { } e && label.StartLine <= e && label.EndLine >= s;
    }
}
