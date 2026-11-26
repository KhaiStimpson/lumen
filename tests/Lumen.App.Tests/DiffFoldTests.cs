using Avalonia.Headless.XUnit;
using Lumen.App.Diff;
using Lumen.App.ViewModels;

namespace Lumen.App.Tests;

/// <summary>Mechanical hunks collapse behind a badge in the diff; group members link to each other.</summary>
public sealed class DiffFoldTests
{
    private const string RenamedOnly = "src/AndrewCrm.Web/Pages/Admin/AiAudit.cshtml.cs";

    [AvaloniaFact]
    public async Task ProvenMechanicalHunksCollapseBehindTheirReasonAndExpand()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateAuditSource());
        try
        {
            await pr.SelectFileAsync(pr.Files.Single(f => f.Path == RenamedOnly));

            var badge = Assert.Single(pr.CurrentDiff!.Rows, r => r.CardId?.StartsWith(DiffFold.CardPrefix, StringComparison.Ordinal) == true);
            Assert.Equal(0, pr.CurrentDiff.Additions);
            var fold = pr.ResolveFold(badge.CardId!)!;
            Assert.StartsWith("pure rename `RedactedAiAuditEntry`→`AiAuditEntry`", fold.Reason, StringComparison.Ordinal);
            Assert.Equal(("2 lines", false, "Open"), (fold.LinesLabel, fold.IsExpanded, fold.ToggleLabel));
            Assert.Equal(("Mechanical · Proven", true, false), (fold.KindLabel, fold.IsMechanical, fold.IsBoilerplate));
            Assert.False(pr.IsFoldOpened(fold.Id));

            fold.ToggleCommand.Execute(null);

            // An opened fold shows every line.
            Assert.True(pr.CurrentDiff!.Additions > 0);
            Assert.True(pr.ResolveFold(badge.CardId!)!.IsExpanded);
            Assert.True(pr.IsFoldOpened(fold.Id));
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task BehaviourChangesAreNeverFolded()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateAuditSource());
        try
        {
            await pr.SelectFileAsync(pr.Files.Single(f => f.Path.EndsWith("Pages/Admin/AiAudit.cshtml", StringComparison.Ordinal)));

            Assert.DoesNotContain(pr.CurrentDiff!.Rows, r => r.CardId?.StartsWith(DiffFold.CardPrefix, StringComparison.Ordinal) == true);
            Assert.True(pr.CurrentDiff.Additions > 0);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task GroupMembersLinkToEachOtherAcrossFiles()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateAuditSource());
        try
        {
            var group = pr.Triage!.Groups.Single(g => g.Title == "Renamed `RedactedAiAuditEntry`→`AiAuditEntry`");
            var members = pr.Triage.Hunks.Where(h => h.GroupId == group.Id).ToList();
            await pr.SelectFileAsync(pr.Files.Single(f => f.Path == members[0].Path));
            var first = pr.SelectedFile!.Path;
            var badge = pr.CurrentDiff!.Rows.First(r => r.CardId?.StartsWith(DiffFold.CardPrefix, StringComparison.Ordinal) == true);
            var fold = pr.ResolveFold(badge.CardId!)!;
            Assert.True(fold.HasGroup);
            Assert.StartsWith("1 of ", fold.GroupLabel, StringComparison.Ordinal);
            Assert.False(fold.HasPrevious);

            await fold.NextCommand.ExecuteAsync(null);

            Assert.Equal(members[1].Path, pr.SelectedFile!.Path);
            Assert.NotNull(pr.FocusRange);
            Assert.True(first != pr.SelectedFile.Path || members[0].NewStart != members[1].NewStart);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }
}
