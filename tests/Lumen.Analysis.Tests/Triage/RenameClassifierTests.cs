using Lumen.Analysis.Tests.Support;
using Lumen.Domain;

namespace Lumen.Analysis.Tests.Triage;

public class RenameClassifierTests
{
    private const string Service = """
        namespace Shop;

        public class OrderService
        {
            private readonly int rate = 2;

            public int Price(int qty)
            {
                var subtotal = qty * rate;
                return Round(subtotal);
            }

            // Rounds to cents.
            private int Round(int value)
            {
                return value / 100 * 100;
            }
        }
        """;

    private static string Rename(string text, string from, string to) =>
        System.Text.RegularExpressions.Regex.Replace(text, $@"\b{from}\b", to);

    [Fact]
    public void RenamedLocalIsPureRename()
    {
        var hunk = TriageHarness.Single(Service, Rename(Service, "subtotal", "gross"));

        Assert.Equal(ChangeClass.Rename, hunk.Class);
        Assert.Equal(TriageTier.Skip, hunk.Tier);
        Assert.Equal(["pure rename `subtotal`→`gross`"], hunk.Reasons);
        Assert.NotNull(hunk.GroupId);
    }

    [Fact]
    public void RenamedPrivateMethodAcrossHunksIsOneGroup()
    {
        var padded = Service.Replace("    // Rounds to cents.", string.Concat(Enumerable.Repeat("    // spacer\n", 10)) + "    // Rounds to cents.", StringComparison.Ordinal);

        var result = TriageHarness.Run(new TestFile("src/OrderService.cs", padded, Rename(padded, "Round", "RoundToCents")));

        Assert.Equal(2, result.Hunks.Count);
        Assert.All(result.Hunks, h => Assert.Equal(ChangeClass.Rename, h.Class));
        var group = Assert.Single(result.Groups);
        Assert.Equal("Renamed `Round`→`RoundToCents`", group.Title);
        Assert.Equal(2, group.Members.Count);
    }

    [Fact]
    public void RenamedTypeAcrossFilesIsOneGroup()
    {
        const string decl = "namespace Shop;\n\npublic class Basket\n{\n    public Basket() { }\n}\n";
        const string use = "namespace Shop;\n\npublic class Checkout\n{\n    private readonly Basket basket = new Basket();\n}\n";

        var result = TriageHarness.Run(
            new TestFile("src/Basket.cs", decl, Rename(decl, "Basket", "Cart")),
            new TestFile("src/Checkout.cs", use, Rename(use, "Basket", "Cart")));

        Assert.All(result.Hunks, h => Assert.Equal(ChangeClass.Rename, h.Class));
        var group = Assert.Single(result.Groups);
        Assert.Equal(["src/Basket.cs", "src/Checkout.cs"], group.Members.Select(m => m.Path));
    }

    [Fact]
    public void DocCommentFollowingTheRenameIsStillPureRename()
    {
        var head = Rename(Service, "Round", "RoundToCents").Replace("// Rounds to cents.", "// RoundToCents rounds to cents.", StringComparison.Ordinal);
        var basis = Service.Replace("// Rounds to cents.", "// Round rounds to cents.", StringComparison.Ordinal);

        var hunk = TriageHarness.Single(basis, head);

        Assert.Equal(ChangeClass.Rename, hunk.Class);
    }

    [Fact]
    public void NearMissRenamePlusChangedLiteralIsNotRename()
    {
        var head = Rename(Service, "subtotal", "gross").Replace("qty * rate", "qty * rate + 1", StringComparison.Ordinal);

        Assert.Equal(ChangeClass.BehaviourChange, TriageHarness.Single(Service, head).Class);
    }

    [Fact]
    public void NearMissInconsistentMapIsNotRename()
    {
        var head = Service
            .Replace("var subtotal = qty * rate;", "var gross = qty * rate;", StringComparison.Ordinal)
            .Replace("return Round(subtotal);", "return Round(net);", StringComparison.Ordinal);

        Assert.Equal(ChangeClass.BehaviourChange, TriageHarness.Single(Service, head).Class);
    }

    [Fact]
    public void PartialRenameIsNotRename()
    {
        var head = Service.Replace("var subtotal = qty * rate;", "var gross = qty * rate;\n        var subtotal = gross;", StringComparison.Ordinal);

        Assert.Equal(ChangeClass.BehaviourChange, TriageHarness.Single(Service, head).Class);
    }

    [Fact]
    public void RenameThatCapturesAnExistingNameIsNotRename()
    {
        // Renaming the local `subtotal` to `rate` now reads the local where the field was read.
        var head = Rename(Service, "subtotal", "rate");

        Assert.Equal(ChangeClass.BehaviourChange, TriageHarness.Single(Service, head).Class);
    }

    [Fact]
    public void SwappedNamesAreNotRename()
    {
        const string before = "class C\n{\n    int M(int a, int b)\n    {\n        return a - b;\n    }\n}\n";
        const string after = "class C\n{\n    int M(int a, int b)\n    {\n        return b - a;\n    }\n}\n";

        Assert.Equal(ChangeClass.BehaviourChange, TriageHarness.Single(before, after).Class);
    }

    [Theory]
    [InlineData("Price", "Cost")]
    [InlineData("qty", "quantity")]
    public void PublicMemberOrPublicParameterRenameStaysReviewable(string from, string to)
    {
        Assert.Equal(ChangeClass.BehaviourChange, TriageHarness.Single(Service, Rename(Service, from, to)).Class);
    }

    [Fact]
    public void EnumMemberRenameStaysReviewable()
    {
        const string before = "enum Status\n{\n    Open,\n    Closed,\n}\n";

        Assert.Equal(ChangeClass.BehaviourChange, TriageHarness.Single(before, Rename(before, "Closed", "Shut")).Class);
    }

    [Fact]
    public void RenameToASymbolDeclaredOutsideThePullRequestIsNotRename()
    {
        // `Console.Out` → `Console.Error`: neither is declared in the PR, so this is switching targets.
        const string before = "class C\n{\n    void M() => System.Console.Out.WriteLine();\n}\n";

        Assert.Equal(ChangeClass.BehaviourChange, TriageHarness.Single(before, before.Replace("Out", "Error", StringComparison.Ordinal)).Class);
    }
}
