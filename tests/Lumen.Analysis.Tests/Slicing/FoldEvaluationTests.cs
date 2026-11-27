using System.Globalization;
using System.Text;
using System.Text.Json;
using Lumen.Domain;
using Lumen.TestSupport;
using Xunit.Abstractions;

namespace Lumen.Analysis.Tests.Slicing;

/// <summary>
/// Phase 5's yardstick: every fold on the fixture against the answer key. No must-not-fold region may fold (the
/// worst bug), and the listed boilerplate regions should. Prints every boilerplate fold with its lines for the
/// hand-read sweep recorded in docs/overhaul.md.
/// </summary>
public sealed class FoldEvaluationTests(ITestOutputHelper output)
{
    [Fact]
    public void NoMustNotFoldRegionFoldsAndTheListedBoilerplateDoes()
    {
        var change = FixtureChange.Value;
        using var key = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureRepository.FixtureDirectory, "answer-key.json")));
        var folds = key.RootElement.GetProperty("folds");
        var text = new StringBuilder();

        bool Folded(string path, int start, int end, FoldKind kind) => change.Triage.Hunks.Any(h =>
            h.Path == path && h.Fold == kind && h.NewStart is { } s && h.NewEnd is { } e && s <= end && e >= start);

        var wrong = new List<string>();
        foreach (var region in folds.GetProperty("mustNotFold").EnumerateArray())
        {
            var (path, start, end) = (region.GetProperty("path").GetString()!, region.GetProperty("startLine").GetInt32(), region.GetProperty("endLine").GetInt32());
            if (Folded(path, start, end, FoldKind.Boilerplate) || Folded(path, start, end, FoldKind.Mechanical))
            {
                wrong.Add($"{Path.GetFileName(path)}:{start}-{end} ({region.GetProperty("why").GetString()})");
            }
        }

        var listed = folds.GetProperty("boilerplate").EnumerateArray().ToList();
        var hit = listed.Count(r => Folded(r.GetProperty("path").GetString()!, r.GetProperty("startLine").GetInt32(), r.GetProperty("endLine").GetInt32(), FoldKind.Boilerplate));
        text.AppendLine(CultureInfo.InvariantCulture, $"Must-not-fold regions folded: {wrong.Count}{(wrong.Count > 0 ? " — " + string.Join("; ", wrong) : "")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Listed boilerplate regions folded: {hit} of {listed.Count}");
        foreach (var region in listed.Where(r => !Folded(r.GetProperty("path").GetString()!, r.GetProperty("startLine").GetInt32(), r.GetProperty("endLine").GetInt32(), FoldKind.Boilerplate)))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  not folded: {Path.GetFileName(region.GetProperty("path").GetString())}:{region.GetProperty("startLine").GetInt32()} {region.GetProperty("shape").GetString()}");
        }

        var changed = change.Triage.Hunks.Sum(h => h.ChangedLines);
        var mechanical = change.Triage.Hunks.Where(h => h.Fold == FoldKind.Mechanical).Sum(h => h.ChangedLines);
        var boilerplate = change.Triage.Hunks.Where(h => h.Fold == FoldKind.Boilerplate).Sum(h => h.ChangedLines);
        text.AppendLine(CultureInfo.InvariantCulture, $"Changed {changed:N0}: mechanical {mechanical:N0}, boilerplate {boilerplate:N0}, open {changed - mechanical - boilerplate:N0} ({(double)(changed - mechanical - boilerplate) / changed:P1})");

        // The hand-read sweep: every boilerplate fold, its reason and its lines.
        var head = Path.Combine(FixtureRepository.FixtureDirectory, "head");
        foreach (var fold in change.Triage.Hunks.Where(h => h.Fold == FoldKind.Boilerplate))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"--- {fold.Path}:{fold.NewStart}-{fold.NewEnd} · {string.Join("; ", fold.Reasons)}");
            var lines = File.ReadAllLines(Path.Combine(head, fold.Path));
            for (var n = fold.NewStart!.Value; n <= fold.NewEnd!.Value && n <= lines.Length; n++)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"    {n,4} {lines[n - 1]}");
            }
        }

        output.WriteLine(text.ToString());
        Assert.Empty(wrong);
        Assert.True(hit == listed.Count, $"only {hit} of {listed.Count} listed boilerplate regions folded");
    }
}
