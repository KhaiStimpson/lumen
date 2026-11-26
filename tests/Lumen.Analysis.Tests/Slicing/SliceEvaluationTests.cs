using System.Globalization;
using System.Text;
using System.Text.Json;
using Lumen.Domain;
using Lumen.Roslyn.Slicing;
using Lumen.TestSupport;
using Xunit.Abstractions;

namespace Lumen.Analysis.Tests.Slicing;

/// <summary>
/// Phase 4's yardstick: the slicer against the fixture's answer key, written by a separate agent that never saw the
/// slicer. Reports file- and member-level precision and recall, Shared and Unlinked placement, slice sizes and the
/// Unlinked + Shared share of the lines to read. The targets are recorded in docs/overhaul.md.
/// </summary>
public sealed class SliceEvaluationTests(ITestOutputHelper output)
{
    /// <summary>Agreed targets (docs/overhaul.md, Phase 4 results). Precision and recall are file-level, micro-averaged.</summary>
    public const double TargetPrecision = 0.85;

    public const double TargetRecall = 0.85;

    public const int MaxSliceFiles = 15;

    public const double MaxUnlinkedSharedShare = 0.35;

    [Fact]
    public void SlicesMatchTheAnswerKey()
    {
        var change = FixtureChange.Value;
        var result = change.Slice();
        var report = Evaluate(result, change);
        output.WriteLine(report.Text);

        Assert.False(result.FellBack, result.FallbackReason);
        Assert.True(report.FilePrecision >= TargetPrecision, $"file precision {report.FilePrecision:P1} < {TargetPrecision:P0}");
        Assert.True(report.FileRecall >= TargetRecall, $"file recall {report.FileRecall:P1} < {TargetRecall:P0}");
        Assert.True(report.LargestSlice <= MaxSliceFiles, $"largest slice has {report.LargestSlice} files");
        Assert.True(report.UnlinkedSharedShare <= MaxUnlinkedSharedShare, $"Unlinked + Shared hold {report.UnlinkedSharedShare:P1} of the lines to read");
    }

    [Fact]
    public void EveryChangedFileLandsSomewhereAndNoSliceMergesAcrossPossibleLinks()
    {
        var change = FixtureChange.Value;
        var result = change.Slice();
        var placed = result.Slices.SelectMany(s => s.Files).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

        Assert.All(change.Facts, f => Assert.Contains(f.Path, placed));

        // Near-misses: the second payment gateway stays out of Checkout; the site-wide script joins no page.
        var checkout = result.Slices.Single(s => s.SeedPath.EndsWith("Orders/Checkout.cshtml", StringComparison.Ordinal));
        Assert.DoesNotContain(checkout.Files, f => f.Path.EndsWith("BankTransferPaymentGateway.cs", StringComparison.Ordinal));
        Assert.Contains(checkout.PossibleLinks, l => l.To.EndsWith("BankTransferPaymentGateway.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Slices.Where(s => s.Kind == SliceKind.Feature).SelectMany(s => s.Files), f => f.Path.EndsWith("wwwroot/js/site.js", StringComparison.Ordinal));

        // Three EditModels, three slices.
        Assert.Equal(3, result.Slices.Count(s => s.SeedPath.EndsWith("/Edit.cshtml", StringComparison.Ordinal)));
    }

    [Fact]
    public void EveryFileSaysWhyItIsInItsSliceAndReadingOrderHolds()
    {
        var result = FixtureChange.Value.Slice();

        foreach (var slice in result.Slices)
        {
            Assert.All(slice.Files, f => Assert.False(string.IsNullOrWhiteSpace(f.Why)));
            Assert.Equal(slice.Files.OrderBy(f => f.Order).Select(f => f.Path), slice.Files.Select(f => f.Path));
        }

        var edit = result.Slices.Single(s => s.SeedPath == "src/Shop.Web/Pages/Orders/Edit.cshtml");
        Assert.Equal("Orders / Edit", edit.Title);
        Assert.Contains(edit.Files, f => f.Path.EndsWith("orders-edit.js", StringComparison.Ordinal) && f.Why == "`Edit.cshtml` loads `orders-edit.js`");
        var view = edit.Files.Single(f => f.Path.EndsWith("Edit.cshtml", StringComparison.Ordinal)).Order;
        var script = edit.Files.Single(f => f.Path.EndsWith("orders-edit.js", StringComparison.Ordinal)).Order;
        Assert.True(view < script);
    }

    [Fact]
    public void LargeSlicesSplitIntoHandlerSections()
    {
        var result = FixtureChange.Value.Slice(new SlicerOptions { SectionFiles = 3, SectionLines = 50 });
        var dashboard = result.Slices.Single(s => s.SeedPath.EndsWith("Admin/Pages/Dashboard.cshtml", StringComparison.Ordinal));

        Assert.Contains("Page", dashboard.Sections);
        Assert.True(dashboard.Sections.Count >= 2, string.Join(", ", dashboard.Sections));
        Assert.All(dashboard.Files, f => Assert.False(string.IsNullOrEmpty(f.Section)));
    }

    // ---------------------------------------------------------------- evaluation

    public sealed record Report(double FilePrecision, double FileRecall, double MemberPrecision, double MemberRecall, int LargestSlice, double UnlinkedSharedShare, string Text);

    internal static Report Evaluate(SliceResult result, FixtureChange change)
    {
        using var key = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureRepository.FixtureDirectory, "answer-key.json")));
        var root = key.RootElement;
        var text = new StringBuilder();
        int tp = 0, predicted = 0, expected = 0, mtp = 0, mpredicted = 0, mexpected = 0;
        var features = result.Slices.Where(s => s.Kind == SliceKind.Feature).ToList();
        var matched = new HashSet<string>(StringComparer.Ordinal);
        text.AppendLine(CultureInfo.InvariantCulture, $"{"slice",-34} {"exp",4} {"got",4} {"tp",4}  missing / extra");
        foreach (var slice in root.GetProperty("slices").EnumerateArray())
        {
            var seed = slice.GetProperty("seed").GetString()!;
            var want = slice.GetProperty("files").EnumerateArray().Select(f => f.GetString()!).ToHashSet(StringComparer.Ordinal);
            var wantMembers = slice.GetProperty("members").EnumerateArray().Select(f => f.GetString()!).ToHashSet(StringComparer.Ordinal);
            var mine = features.FirstOrDefault(s => s.SeedPath == seed);
            var got = mine?.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal) ?? [];
            var gotMembers = mine?.Files.SelectMany(f => f.Members.Select(m => $"{f.Path}::{m}")).ToHashSet(StringComparer.Ordinal) ?? [];
            if (mine is not null)
            {
                matched.Add(mine.Id);
            }

            var hit = got.Intersect(want).Count();
            tp += hit;
            predicted += got.Count;
            expected += want.Count;
            mtp += gotMembers.Intersect(wantMembers).Count();
            mpredicted += gotMembers.Count;
            mexpected += wantMembers.Count;
            text.AppendLine(CultureInfo.InvariantCulture, $"{slice.GetProperty("name").GetString(),-34} {want.Count,4} {got.Count,4} {hit,4}  -[{Short(want.Except(got))}] +[{Short(got.Except(want))}]");
            if (Environment.GetEnvironmentVariable("LUMEN_SLICE_MEMBERS") == "1")
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"    members -[{string.Join(", ", wantMembers.Except(gotMembers).Select(m => m.Split("::")[1]))}]");
                text.AppendLine(CultureInfo.InvariantCulture, $"    members +[{string.Join(", ", gotMembers.Except(wantMembers).Select(m => m.Split("::")[1]))}]");
            }
        }

        foreach (var extra in features.Where(s => !matched.Contains(s.Id)))
        {
            predicted += extra.Files.Count;
            text.AppendLine(CultureInfo.InvariantCulture, $"(extra) {extra.Title,-26} {0,4} {extra.Files.Count,4} {0,4}  +[{Short(extra.Files.Select(f => f.Path))}]");
        }

        // Shared and Unlinked count as two more "slices" each, so misplacing a file there costs precision and recall too.
        void Bucket(string name, IEnumerable<string> want, SliceKind kind)
        {
            var wantSet = want.ToHashSet(StringComparer.Ordinal);
            var got = result.Slices.Where(s => s.Kind == kind).SelectMany(s => s.Files).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            var hit = got.Intersect(wantSet).Count();
            tp += hit;
            predicted += got.Count;
            expected += wantSet.Count;
            text.AppendLine(CultureInfo.InvariantCulture, $"{name,-34} {wantSet.Count,4} {got.Count,4} {hit,4}  -[{Short(wantSet.Except(got))}] +[{Short(got.Except(wantSet))}]");
        }

        Bucket("Shared", root.GetProperty("shared").EnumerateArray().Select(e => e.GetString()!), SliceKind.Shared);
        var unlinked = root.GetProperty("unlinked");
        Bucket("Unlinked · used outside", unlinked.GetProperty("usedOutside").EnumerateArray().Select(e => e.GetString()!), SliceKind.UnlinkedUsed);
        Bucket("Unlinked · nothing uses it", unlinked.GetProperty("nothingUses").EnumerateArray().Select(e => e.GetString()!), SliceKind.UnlinkedUnused);
        Bucket("Mechanical only", root.GetProperty("mechanicalOnly").EnumerateArray().Select(e => e.GetString()!), SliceKind.MechanicalOnly);

        var precision = predicted == 0 ? 0 : (double)tp / predicted;
        var recall = expected == 0 ? 0 : (double)tp / expected;
        var memberPrecision = mpredicted == 0 ? 0 : (double)mtp / mpredicted;
        var memberRecall = mexpected == 0 ? 0 : (double)mtp / mexpected;
        var largest = features.Count == 0 ? 0 : features.Max(s => s.Files.Count);
        var open = change.Facts.ToDictionary(f => f.Path, f => f.OpenLines, StringComparer.Ordinal);
        var toRead = change.Facts.Sum(f => f.OpenLines);
        var unlinkedShared = result.Slices.Where(s => s.Kind is SliceKind.Shared or SliceKind.UnlinkedUsed or SliceKind.UnlinkedUnused)
            .SelectMany(s => s.Files).Sum(f => open.GetValueOrDefault(f.Path));
        var share = toRead == 0 ? 0 : (double)unlinkedShared / toRead;
        text.AppendLine(CultureInfo.InvariantCulture, $"File precision {precision:P1} ({tp}/{predicted}), recall {recall:P1} ({tp}/{expected})");
        text.AppendLine(CultureInfo.InvariantCulture, $"Member precision {memberPrecision:P1} ({mtp}/{mpredicted}), recall {memberRecall:P1} ({mtp}/{mexpected})");
        text.AppendLine(CultureInfo.InvariantCulture, $"Largest feature slice: {largest} files; slices: {features.Count} feature, {result.Slices.Count - features.Count} other");
        text.AppendLine(CultureInfo.InvariantCulture, $"Unlinked + Shared: {unlinkedShared:N0} of {toRead:N0} lines to read ({share:P1}); sliced in {result.Duration.TotalMilliseconds:N0} ms");
        return new Report(precision, recall, memberPrecision, memberRecall, largest, share, text.ToString());
    }

    private static string Short(IEnumerable<string> paths) => string.Join(", ", paths.Select(Path.GetFileName));
}
