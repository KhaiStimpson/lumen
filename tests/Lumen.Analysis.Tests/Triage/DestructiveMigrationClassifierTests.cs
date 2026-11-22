using Lumen.Analysis.Tests.Support;
using Lumen.Domain;

namespace Lumen.Analysis.Tests.Triage;

public class DestructiveMigrationClassifierTests
{
    private const string Path = "src/Data/Migrations/20260101_Invoices.cs";

    private static string Migration(string up) => $$"""
        using Microsoft.EntityFrameworkCore.Migrations;

        namespace Shop.Data.Migrations;

        public partial class Invoices : Migration
        {
            protected override void Up(MigrationBuilder migrationBuilder)
            {
                migrationBuilder.AddColumn<string>(
                    name: "Notes",
                    table: "Invoices",
                    nullable: true);
        {{up}}
            }

            protected override void Down(MigrationBuilder migrationBuilder)
            {
                migrationBuilder.DropColumn(name: "Notes", table: "Invoices");
            }
        }
        """;

    private static IReadOnlyList<HunkTriage> Added(string up) =>
        TriageHarness.Run(new TestFile(Path, null, Migration(up))).Hunks;

    [Fact]
    public void DropColumnIsCriticalAndTheRestStaysSkip()
    {
        var hunks = Added("""
                    migrationBuilder.DropColumn(
                        name: "Total",
                        table: "Invoices");
            """);

        var critical = Assert.Single(hunks, h => h.Tier == TriageTier.Critical);
        Assert.Equal(["drops column `Invoices.Total`"], critical.Reasons);
        var line = TestDiffs.LineOf(Migration("migrationBuilder.DropColumn("), "migrationBuilder.DropColumn(");
        Assert.Equal((line, line + 2), (critical.NewStart, critical.NewEnd));
        Assert.All(hunks.Where(h => h != critical), h => Assert.Equal((ChangeClass.Generated, TriageTier.Skip), (h.Class, h.Tier)));
    }

    [Theory]
    [InlineData("migrationBuilder.DropTable(name: \"Payments\");", "drops table `Payments`")]
    [InlineData("migrationBuilder.RenameColumn(name: \"Total\", table: \"Invoices\", newName: \"Amount\");", "renames column `Invoices.Total` to `Amount`")]
    [InlineData("migrationBuilder.RenameTable(name: \"Invoices\", newName: \"Bills\");", "renames table `Invoices` to `Bills`")]
    [InlineData("migrationBuilder.Sql(\"UPDATE Invoices SET Total = 0\");", "runs raw SQL")]
    [InlineData("migrationBuilder.DeleteData(table: \"Plans\", keyColumn: \"Id\", keyValue: 3);", "deletes rows from `Plans`")]
    [InlineData("migrationBuilder.AlterColumn<string>(name: \"Code\", table: \"Invoices\", maxLength: 20, oldMaxLength: 100);", "narrows column `Invoices.Code` to 20 characters")]
    [InlineData("migrationBuilder.AlterColumn<int>(name: \"Qty\", table: \"Lines\", type: \"int\", oldType: \"bigint\");", "changes column `Lines.Qty` from `bigint` to `int`")]
    [InlineData("migrationBuilder.AlterColumn<string>(name: \"Email\", table: \"Users\", nullable: false, oldNullable: true);", "makes column `Users.Email` required")]
    public void DestructiveOperationsAreCriticalInWords(string statement, string reason)
    {
        var critical = Assert.Single(Added("        " + statement), h => h.Tier == TriageTier.Critical);

        Assert.Equal([reason], critical.Reasons);
    }

    [Fact]
    public void WideningAlterColumnIsNotCritical()
    {
        var hunks = Added("        migrationBuilder.AlterColumn<string>(name: \"Code\", table: \"Invoices\", maxLength: 200, oldMaxLength: 100);");

        Assert.DoesNotContain(hunks, h => h.Tier == TriageTier.Critical);
    }

    [Fact]
    public void DropsInDownAreNotCritical()
    {
        var hunks = Added(string.Empty);

        Assert.All(hunks, h => Assert.Equal((ChangeClass.Generated, TriageTier.Skip), (h.Class, h.Tier)));
    }

    [Fact]
    public void AddedMigrationWorksWithoutLoadedSources()
    {
        var diff = TriageHarness.Diff(Path, null, Migration("        migrationBuilder.DropTable(name: \"Payments\");"));

        var hunks = Roslyn.Triage.RoslynTriage.CreatePipeline().Run(TestDiffs.Snapshot(diff)).Hunks;

        Assert.Contains(hunks, h => h.Tier == TriageTier.Critical && h.Reasons.SequenceEqual(["drops table `Payments`"]));
    }
}
