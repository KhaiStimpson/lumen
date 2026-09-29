using Avalonia.Headless.XUnit;
using Lumen.App.ViewModels;
using Lumen.Contracts;

namespace Lumen.App.Tests;

public sealed class CoverageTests
{
    private static TriageReady Triage()
    {
        var ready = new TriageReady();
        ready.Hunks.Add(new HunkTriage { Path = "a.cs", NewStart = 1, NewEnd = 5, Tier = TriageTier.Critical, ChangedLines = 10 });
        ready.Hunks.Add(new HunkTriage { Path = "b.cs", NewStart = 1, NewEnd = 5, Tier = TriageTier.WorthALook, ChangedLines = 30, GroupId = "g1" });
        ready.Hunks.Add(new HunkTriage { Path = "c.cs", NewStart = 10, NewEnd = 20, Tier = TriageTier.WorthALook, ChangedLines = 60 });
        ready.Hunks.Add(new HunkTriage { Path = "d.cs", NewStart = 1, NewEnd = 99, Tier = TriageTier.Skip, ChangedLines = 900 });
        ready.Groups.Add(new TriageGroup { Id = "g1", Title = "t" });
        return ready;
    }

    [Fact]
    public void CountsOnlyCriticalAndWorthALookLines()
    {
        Assert.Equal((0, 100), ReviewPlanBuilder.Coverage(Triage(), [], new HashSet<string>(), new HashSet<string>()));
    }

    [Fact]
    public void ViewedFilesAcknowledgedGroupsHandledPointsAndVisitsAllCount()
    {
        var triage = Triage();
        triage.Groups[0].Acknowledged = true;
        var handled = new ReviewPointViewModel(new ReviewPoint { Id = "p", Anchor = new CodeLocation { Path = "c.cs", StartLine = 12 } }, 1)
        {
            State = ReviewPointState.Dismissed,
        };

        Assert.Equal((30, 100), ReviewPlanBuilder.Coverage(triage, [], new HashSet<string>(), new HashSet<string>()));
        Assert.Equal((40, 100), ReviewPlanBuilder.Coverage(triage, [], new HashSet<string> { "a.cs" }, new HashSet<string>()));
        Assert.Equal((90, 100), ReviewPlanBuilder.Coverage(triage, [handled], new HashSet<string>(), new HashSet<string>()));
        Assert.Equal((40, 100), ReviewPlanBuilder.Coverage(triage, [], new HashSet<string>(), new HashSet<string> { DiffFolds.IdFor(triage.Hunks[0]) }));
    }

    [AvaloniaFact]
    public async Task OpeningChangesFromThePlanRaisesCoverage()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            var before = pr.CoverageFraction;
            Assert.StartsWith("You've covered ", pr.CoverageLabel, StringComparison.Ordinal);
            Assert.EndsWith("% covered", pr.CoverageShort, StringComparison.Ordinal);

            var more = pr.ReviewPlan.Sections.Single(s => s.Key == "WorthALook").Items.OfType<MoreChangesViewModel>().Single();
            await pr.SelectMoreChangesAsync(more);

            Assert.True(pr.CoverageFraction > before);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }
}
