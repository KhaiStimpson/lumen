using System.Globalization;
using System.Text;
using System.Text.Json;
using Lumen.Domain;
using Lumen.TestSupport;
using Xunit.Abstractions;

namespace Lumen.Analysis.Tests.Slicing;

/// <summary>
/// Phase 7's safety sweep on the fixture: every red flag against the answer key's planted flags, its near-misses
/// (notFlagged) and the flags it calls debatable (left out of the score). Prints every flag for the hand-read.
/// </summary>
public sealed class FlagEvaluationTests(ITestOutputHelper output)
{
    private sealed record Expected(FlagKind Kind, string Path, DiffSide Side, int Line, string Text);

    [Fact]
    public void EveryPlantedFlagIsFoundAndNoNearMissIs()
    {
        var flags = FixtureChange.Value.Triage.Flags;
        using var key = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureRepository.FixtureDirectory, "answer-key.json")));
        var root = key.RootElement;
        static DiffSide Side(JsonElement e) => e.GetProperty("side").GetString() == "base" ? DiffSide.Left : DiffSide.Right;
        var planted = root.GetProperty("flags").EnumerateArray()
            .Select(e => new Expected(Enum.Parse<FlagKind>(e.GetProperty("kind").GetString()!), e.GetProperty("path").GetString()!, Side(e), e.GetProperty("line").GetInt32(), e.GetProperty("text").GetString()!)).ToList();
        var nearMisses = root.GetProperty("notFlagged").EnumerateArray().Select(e => (Path: e.GetProperty("path").GetString()!, Side: Side(e), Line: e.GetProperty("line").GetInt32())).ToList();
        var debatable = root.GetProperty("debatableFlags").EnumerateArray().Select(e => (Path: e.GetProperty("path").GetString()!, Side: Side(e), Line: e.GetProperty("line").GetInt32())).ToList();

        bool Matches(RedFlag f, Expected e) => f.Kind == e.Kind && f.Path == e.Path && f.Side == e.Side && f.Line == e.Line;
        var found = planted.Where(e => flags.Any(f => Matches(f, e))).ToList();
        var missed = planted.Except(found).ToList();
        var scored = flags.Where(f => !debatable.Any(d => d.Path == f.Path && d.Side == f.Side && d.Line == f.Line)).ToList();
        var extra = scored.Where(f => !planted.Any(e => Matches(f, e))).ToList();
        var nearMissFlagged = flags.Where(f => nearMisses.Any(n => n.Path == f.Path && n.Side == f.Side && n.Line == f.Line)).ToList();

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Flags: {flags.Count}; planted found {found.Count} of {planted.Count} (recall {(double)found.Count / planted.Count:P1}); precision {(double)(scored.Count - extra.Count) / Math.Max(1, scored.Count):P1} ({scored.Count - extra.Count} of {scored.Count} scored); near-misses flagged {nearMissFlagged.Count} of {nearMisses.Count}");
        foreach (var e in missed)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  MISSED {e.Kind} {e.Path}:{e.Side}:{e.Line} {e.Text}");
        }

        foreach (var f in extra)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  EXTRA {f.Kind} {f.Path}:{f.Side}:{f.Line} {f.Message}");
        }

        foreach (var f in flags)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {f.Kind,-22} {f.Path}:{(f.Side == DiffSide.Left ? "base" : "head")}:{f.Line} [{f.Member}] {f.Message}");
        }

        output.WriteLine(text.ToString());
        Assert.Empty(missed);
        Assert.Empty(nearMissFlagged);
        Assert.Empty(extra);
    }

    [Fact]
    public void NoFoldHidesAFlag()
    {
        var triage = FixtureChange.Value.Triage;
        foreach (var flag in triage.Flags)
        {
            Assert.DoesNotContain(triage.Hunks, h => h.Path == flag.Path && h.Fold != FoldKind.Open && (flag.Side == DiffSide.Right
                ? h.NewStart <= flag.Line && h.NewEnd >= flag.Line
                : h.OldStart <= flag.Line && h.OldEnd >= flag.Line));
        }
    }
}
