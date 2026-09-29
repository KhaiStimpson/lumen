using System.Globalization;
using Lumen.Domain;

namespace Lumen.Analysis;

/// <summary>The one-line account of where a pull request's changed lines went, in words a reviewer can act on.</summary>
public static class TriageSummaryText
{
    /// <summary>Lines of a mechanical class that a proof put in Skip.</summary>
    public static int ProvenMechanical(TriageResult triage) =>
        triage.Hunks.Where(h => h.Class.IsMechanical() && h.Tier == TriageTier.Skip).Sum(h => h.ChangedLines);

    /// <summary>Lines of a mechanical class left above Skip: ripple call sites, moves into another type.</summary>
    public static int LikelyMechanical(TriageResult triage) =>
        triage.Hunks.Where(h => h.Class.IsMechanical() && h.Tier != TriageTier.Skip).Sum(h => h.ChangedLines);

    /// <summary>For example "19,700 lines changed: 14,100 proven mechanical, 3,200 new code, 2,400 changing existing behaviour".</summary>
    public static string Describe(TriageResult triage)
    {
        var total = triage.Hunks.Sum(h => h.ChangedLines);
        if (total == 0)
        {
            return "No lines changed";
        }

        var parts = new List<string>();
        Add(parts, ProvenMechanical(triage), "proven mechanical");
        Add(parts, LikelyMechanical(triage), "mechanical but worth a skim");
        Add(parts, Lines(triage, ChangeClass.NewCode), "new code");
        Add(parts, Lines(triage, ChangeClass.BehaviourChange), "changing existing behaviour");

        return $"{Number(total)} {(total == 1 ? "line" : "lines")} changed: {string.Join(", ", parts)}";
    }

    private static int Lines(TriageResult triage, ChangeClass cls) =>
        triage.Hunks.Where(h => h.Class == cls).Sum(h => h.ChangedLines);

    private static void Add(List<string> parts, int lines, string what)
    {
        if (lines > 0)
        {
            parts.Add($"{Number(lines)} {what}");
        }
    }

    private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
