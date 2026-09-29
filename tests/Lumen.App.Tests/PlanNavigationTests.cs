using Avalonia.Headless.XUnit;
using Lumen.App.ViewModels;
using Lumen.Contracts;

namespace Lumen.App.Tests;

/// <summary>J/K walk the plan in tier order; A clears the group the reviewer is on.</summary>
public sealed class PlanNavigationTests
{
    private static int SectionRank(PullRequestViewModel pr, object stop) =>
        pr.ReviewPlan.Sections.ToList().FindIndex(s => s.Items.Contains(stop));

    [AvaloniaFact]
    public async Task StopsFollowTheTiersThenConsistencyNotes()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateRefactorSource());
        try
        {
            var stops = pr.PlanStops();

            Assert.NotEmpty(stops.OfType<TriageGroupViewModel>());
            Assert.NotEmpty(stops.OfType<ReviewPointViewModel>());
            var ranks = stops.Select(s => SectionRank(pr, s)).ToList();
            Assert.Equal(ranks.Order(), ranks);
            var groupTiers = stops.OfType<TriageGroupViewModel>().Select(g => ReviewPlanBuilder.Rank(g.Tier)).ToList();
            Assert.Equal(groupTiers.Order(), groupTiers);
            Assert.All(stops.SkipWhile(s => s is not ReviewPointViewModel), s => Assert.IsType<ReviewPointViewModel>(s));
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    /// <summary>Opening a pull request already puts the reviewer on its first review point; step from wherever that is.</summary>
    private static async Task<TriageGroupViewModel> StepToFirstGroupAsync(PullRequestViewModel pr)
    {
        for (var i = 0; i < pr.PlanStops().Count && pr.CurrentStop is not TriageGroupViewModel; i++)
        {
            await pr.NextPointAsync();
        }

        return Assert.IsType<TriageGroupViewModel>(pr.CurrentStop);
    }

    [AvaloniaFact]
    public async Task JAndKWalkGroupsAndPointsInPlanOrderAndWrap()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateRefactorSource());
        try
        {
            var stops = pr.PlanStops();
            var start = pr.CurrentStop is { } s ? stops.IndexOf(s) : -1;

            await pr.NextPointAsync();
            var expected = stops[(start + 1) % stops.Count];
            Assert.Same(expected, pr.CurrentStop);
            Assert.Equal($"{stops.IndexOf(expected) + 1} of {stops.Count}", pr.PositionLabel);

            var group = await StepToFirstGroupAsync(pr);
            Assert.Same(stops[0], group);
            Assert.True(group.IsCurrent);
            Assert.Null(pr.CurrentPoint);
            Assert.Equal(group.Members[0].Path, pr.SelectedFile!.Path);

            await pr.NextPointAsync();
            Assert.Same(stops[1], pr.CurrentStop);
            Assert.False(group.IsCurrent);

            await pr.PreviousPointAsync();
            await pr.PreviousPointAsync();
            Assert.Same(stops[^1], pr.CurrentStop);
            Assert.IsType<ReviewPointViewModel>(pr.CurrentStop);
            Assert.Equal($"{stops.Count} of {stops.Count}", pr.PositionLabel);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task AAcknowledgesTheCurrentGroupWhichFadesButStaysReachable()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateRefactorSource());
        try
        {
            var group = await StepToFirstGroupAsync(pr);

            await pr.AcknowledgeCurrentGroupAsync();
            await Fixture.WaitUntilAsync(() => group.Acknowledged);

            Assert.Contains(group, pr.PlanStops());
            Assert.Contains(pr.ReviewPlan.Sections, s => s.Items.Contains(group));

            await pr.AcknowledgeCurrentGroupAsync();
            await Fixture.WaitUntilAsync(() => !group.Acknowledged);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task AOnAReviewPointDoesNothing()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            Assert.IsType<ReviewPointViewModel>(pr.CurrentStop);

            await pr.AcknowledgeCurrentGroupAsync();

            Assert.DoesNotContain(pr.ReviewPlan.Groups, g => g.Acknowledged);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task DismissedPointsDropOutOfTheWalk()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateSource());
        try
        {
            var before = pr.PlanStops().Count;
            var point = Assert.IsType<ReviewPointViewModel>(pr.CurrentStop);

            await pr.DismissAsync(point);
            await Fixture.WaitUntilAsync(() => point.IsDismissed);

            Assert.Equal(before - 1, pr.PlanStops().Count);
            Assert.DoesNotContain(point, pr.PlanStops());
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }
}
