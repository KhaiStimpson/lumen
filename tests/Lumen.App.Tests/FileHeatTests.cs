using Avalonia.Headless.XUnit;
using Lumen.App.ViewModels;
using Lumen.Contracts;

namespace Lumen.App.Tests;

public sealed class FileHeatTests
{
    private static FileEntryViewModel File(TierLines? lines) =>
        new(new ChangedFileSummary { Path = "src/A.cs", Additions = 3, Deletions = 1 }) { TierLines = lines };

    [Fact]
    public void AllSkipLinesMakeAFileMechanicalOnly()
    {
        var file = File(new TierLines { Skip = 12 });

        Assert.True(file.IsMechanicalOnly);
        Assert.Equal("12 lines mechanical", file.MechanicalLabel);
        Assert.False(file.HasCritical);
    }

    [Fact]
    public void AnyLineAboveSkipKeepsTheFileInFullView()
    {
        Assert.False(File(new TierLines { Skip = 12, Skim = 1 }).IsMechanicalOnly);
        Assert.False(File(null).IsMechanicalOnly);
        Assert.False(File(new TierLines()).IsMechanicalOnly);
        Assert.Equal("1 line mechanical", File(new TierLines { Skip = 1, WorthALook = 4 }).MechanicalLabel);
    }

    [Fact]
    public void CriticalLinesMarkTheFile()
    {
        Assert.True(File(new TierLines { Critical = 1, Skip = 40 }).HasCritical);
    }

    [AvaloniaFact]
    public async Task RenamedOnlyFilesAreGreyedAndGeneratedFilesCountMechanicalLines()
    {
        var pr = await Fixture.LoadAsync(Fixture.CreateAuditSource());
        try
        {
            var mechanical = pr.Files.Where(f => !f.IsMechanical && f.IsMechanicalOnly).Select(f => f.Path).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(["src/AndrewCrm.Web/Pages/Admin/AiAudit.cshtml.cs", "src/AndrewCrm.Web/Pages/Settings/AiAudit.cshtml.cs"], mechanical);
            Assert.Equal("6 generated files · 3,935 lines", pr.GeneratedSummary);
            Assert.DoesNotContain(pr.Files, f => f.Path.EndsWith("AiLifecycleService.cs", StringComparison.Ordinal) && f.IsMechanicalOnly);
        }
        finally
        {
            await pr.DisposeAsync();
        }
    }
}
