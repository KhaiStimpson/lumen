using Avalonia.Headless.XUnit;
using Lumen.App.ViewModels;
using Lumen.Contracts;

namespace Lumen.App.Tests;

public sealed class ReviewPlanTests
{
    private static HunkTriage Hunk(string path, int start, int end, TriageTier tier, ChangeClass cls = ChangeClass.BehaviourChange, string group = "", int lines = 2)
    {
        var hunk = new HunkTriage { Path = path, NewStart = start, NewEnd = end, Tier = tier, ChangeClass = cls, GroupId = group, ChangedLines = lines };
        hunk.Reasons.Add(cls == ChangeClass.NewCode ? "new file" : "changes existing code");
        return hunk;
    }

    private static ReviewPointViewModel Point(string id, string type, string path, int line) =>
        new(new ReviewPoint { Id = id, Type = type, Title = id, Anchor = new CodeLocation { Path = path, StartLine = line, EndLine = line } }, 1);

    private static TriageReady Triage()
    {
        var ready = new TriageReady { Summary = new TriageSummary { Text = "40 lines changed", TierLines = new TierLines { Critical = 2, WorthALook = 4, Skim = 10, Skip = 24 } } };
        ready.Hunks.Add(Hunk("src/Scheduler.cs", 40, 50, TriageTier.Critical));
        ready.Hunks.Add(Hunk("src/Service.cs", 10, 12, TriageTier.WorthALook));
        ready.Hunks.Add(Hunk("src/Other.cs", 5, 6, TriageTier.WorthALook));
        ready.Hunks.Add(Hunk("src/New.cs", 1, 10, TriageTier.Skim, ChangeClass.NewCode, lines: 10));
        ready.Hunks.Add(Hunk("src/A.cs", 3, 3, TriageTier.Skip, ChangeClass.Rename, "g1", 12));
        ready.Hunks.Add(Hunk("src/B.cs", 7, 7, TriageTier.Skip, ChangeClass.Rename, "g1", 12));
        ready.Groups.Add(new TriageGroup { Id = "g1", ChangeClass = ChangeClass.Rename, Title = "Renamed `Foo`→`Bar`", MemberCount = 2, ChangedLines = 24 });
        return ready;
    }

    [Fact]
    public void PointsLandInTheTierOfTheHunkTheyAreAnchoredIn()
    {
        var critical = Point("risk", "CorrectnessRisk", "src/Scheduler.cs", 45);
        var look = Point("look", "BehaviourChange", "src/Service.cs", 11);

        var plan = ReviewPlanBuilder.Build(Triage(), [critical, look], []);

        Assert.Equal(["Critical", "WorthALook", "Skim", "Skip"], plan.Sections.Select(s => s.Key));
        Assert.Same(critical, plan.Sections[0].Items.Single());
        Assert.Same(look, plan.Sections[1].Items[0]);
    }

    [Fact]
    public void HunksNoPointCoversFoldIntoOneRowPerTier()
    {
        var look = Point("look", "BehaviourChange", "src/Service.cs", 11);

        var plan = ReviewPlanBuilder.Build(Triage(), [look], []);

        var more = Assert.IsType<MoreChangesViewModel>(plan.Sections[1].Items[1]);
        Assert.Equal("src/Other.cs", Assert.Single(more.Hunks).Path);
        Assert.Equal("1 more change in 1 file · 2 lines", more.Label);
        var critical = Assert.IsType<MoreChangesViewModel>(plan.Sections[0].Items.Single());
        Assert.Equal("1 change in 1 file · 2 lines", critical.Label);
        Assert.Equal(new PlanTarget("src/Scheduler.cs", 40, 50), critical.Target);
    }

    [Fact]
    public void GroupsSitInTheirTierWithCountsInWordsAndAreFoldedInSkip()
    {
        var plan = ReviewPlanBuilder.Build(Triage(), [], []);

        var skip = plan.Sections.Single(s => s.Key == "Skip");
        Assert.True(skip.IsFoldable);
        Assert.False(skip.IsExpanded);
        var group = Assert.IsType<TriageGroupViewModel>(skip.Items.Single());
        Assert.Equal("Renamed `Foo`→`Bar`", group.Title);
        Assert.Equal("2 hunks in 2 files · 24 lines", group.Caption);
        Assert.Equal("24 lines", skip.CountLabel);
    }

    [Fact]
    public void PeerPatternNotesGoToAFoldedConsistencySectionLast()
    {
        var note = Point("note", "PatternDeviation", "src/Scheduler.cs", 45);

        var plan = ReviewPlanBuilder.Build(Triage(), [note], []);

        var last = plan.Sections[^1];
        Assert.Equal(("Consistency", false, "1 note"), (last.Key, last.IsExpanded, last.CountLabel));
        Assert.Same(note, last.Items.Single());
        Assert.DoesNotContain(plan.Sections.Take(plan.Sections.Count - 1), s => s.Items.Contains(note));
    }

    [Fact]
    public void GroupStateSurvivesARebuildAndAcknowledgementIsTakenFromTheWire()
    {
        var state = new Dictionary<string, TriageGroupViewModel>(StringComparer.Ordinal);
        var triage = Triage();
        var first = (TriageGroupViewModel)ReviewPlanBuilder.Build(triage, [], state).Sections.Single(s => s.Key == "Skip").Items[0];
        first.IsCurrent = true;

        triage.Groups[0].Acknowledged = true;
        var second = (TriageGroupViewModel)ReviewPlanBuilder.Build(triage, [], state).Sections.Single(s => s.Key == "Skip").Items[0];

        Assert.Same(first, second);
        Assert.True(second.IsCurrent);
        Assert.True(second.Acknowledged);
    }

    [Fact]
    public void WithoutTriageRiskPointsAreWorthALook()
    {
        var look = Point("look", "BehaviourChange", "src/Service.cs", 11);

        var plan = ReviewPlanBuilder.Build(null, [look], []);

        Assert.Equal("WorthALook", Assert.Single(plan.Sections).Key);
        Assert.Equal("1 place to review", plan.ItemsLabel);
    }

    [Fact]
    public void ItemsLabelCountsPlacesInCriticalAndWorthALookOnly()
    {
        var plan = ReviewPlanBuilder.Build(Triage(), [Point("look", "BehaviourChange", "src/Service.cs", 11)], []);

        Assert.Equal("3 places to review", plan.ItemsLabel);
    }

    [AvaloniaFact]
    public async Task ReplayBuildsThePlanForAndrewCrm58()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            var plan = pr.ReviewPlan;
            Assert.True(plan.HasTriage);
            Assert.StartsWith("19,743 lines changed", plan.Summary, StringComparison.Ordinal);
            Assert.Equal(["WorthALook", "Skim", "Skip", "Consistency"], plan.Sections.Select(s => s.Key));
            Assert.Equal(pr.ReviewPoints.Count, plan.Sections[^1].Items.Count);
            Assert.Equal(15_864d, plan.TierLines[3]);
            Assert.All(pr.Files, f => Assert.NotNull(f.TierLines));
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task PickingFoldedChangesFocusesTheirFirstHunk()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            var more = pr.ReviewPlan.Sections.Single(s => s.Key == "WorthALook").Items.OfType<MoreChangesViewModel>().Single();

            await pr.SelectMoreChangesAsync(more);

            Assert.Null(pr.CurrentPoint);
            Assert.Equal(more.Target!.Path, pr.SelectedFile!.Path);
            Assert.Equal((more.Target.StartLine, more.Target.EndLine), pr.FocusRange);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }
}
