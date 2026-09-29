using Lumen.Analysis.Tests.Support;
using Lumen.Domain;

namespace Lumen.Analysis.Tests.Triage;

public class RippleClassifierTests
{
    private const string Pricing = """
        namespace Shop;

        public class Pricing
        {
            public int Quote(int qty, int unit)
            {
                return qty * unit;
            }
        }
        """;

    private const string Checkout = """
        namespace Shop;

        public class Checkout
        {
            private readonly Pricing pricing = new Pricing();

            public int Total(int qty) => pricing.Quote(qty, 250);

            public int Sample() => pricing.Quote(1, 100) + 1;
        }
        """;

    [Fact]
    public void AddedParameterGroupsCallSitesAndKeepsTheSignatureReviewable()
    {
        var result = TriageHarness.Run(
            new TestFile("src/Pricing.cs", Pricing, Pricing.Replace("int qty, int unit)", "int qty, int unit, int discount)", StringComparison.Ordinal).Replace("qty * unit", "qty * unit - discount", StringComparison.Ordinal)),
            new TestFile("src/Checkout.cs", Checkout, Checkout.Replace("Quote(qty, 250)", "Quote(qty, 250, 0)", StringComparison.Ordinal).Replace("Quote(1, 100)", "Quote(1, 100, 5)", StringComparison.Ordinal)));

        var declaration = Assert.Single(result.Hunks, h => h.Path == "src/Pricing.cs");
        Assert.Equal((ChangeClass.BehaviourChange, TriageTier.WorthALook), (declaration.Class, declaration.Tier));
        Assert.Contains("changes the signature of `Quote`: added parameter `discount`", declaration.Reasons);
        Assert.Null(declaration.GroupId);

        var calls = result.Hunks.Where(h => h.Path == "src/Checkout.cs").ToList();
        Assert.All(calls, h => Assert.Equal((ChangeClass.Ripple, TriageTier.Skim), (h.Class, h.Tier)));
        Assert.Equal("Call sites follow the new signature of `Quote`", Assert.Single(result.Groups).Title);
    }

    [Fact]
    public void RemovedParameterCallSitesAreRipple()
    {
        var result = TriageHarness.Run(
            new TestFile("src/Pricing.cs", Pricing, Pricing.Replace("int qty, int unit)", "int qty)", StringComparison.Ordinal).Replace("qty * unit", "qty * 250", StringComparison.Ordinal)),
            new TestFile("src/Checkout.cs", Checkout, Checkout.Replace("Quote(qty, 250)", "Quote(qty)", StringComparison.Ordinal).Replace("Quote(1, 100)", "Quote(1)", StringComparison.Ordinal)));

        Assert.All(result.Hunks.Where(h => h.Path == "src/Checkout.cs"), h => Assert.Equal(ChangeClass.Ripple, h.Class));
    }

    [Fact]
    public void RenamedPublicMethodCallSitesAreRippleAndDeclarationStaysReviewable()
    {
        static string R(string s) => s.Replace("Quote", "PriceFor", StringComparison.Ordinal);

        var result = TriageHarness.Run(new TestFile("src/Pricing.cs", Pricing, R(Pricing)), new TestFile("src/Checkout.cs", Checkout, R(Checkout)));

        var declaration = Assert.Single(result.Hunks, h => h.Path == "src/Pricing.cs");
        Assert.Equal(ChangeClass.BehaviourChange, declaration.Class);
        Assert.Contains(declaration.Reasons, r => r.StartsWith("renames `Quote`→`PriceFor`", StringComparison.Ordinal));
        Assert.All(result.Hunks.Where(h => h.Path == "src/Checkout.cs"), h => Assert.Equal((ChangeClass.Ripple, TriageTier.Skim), (h.Class, h.Tier)));
    }

    [Fact]
    public void NearMissCallSiteThatAlsoChangesAnArgumentIsNotRipple()
    {
        var result = TriageHarness.Run(
            new TestFile("src/Pricing.cs", Pricing, Pricing.Replace("int qty, int unit)", "int qty, int unit, int discount)", StringComparison.Ordinal)),
            new TestFile("src/Checkout.cs", Checkout, Checkout.Replace("Quote(qty, 250)", "Quote(qty, 260, 0)", StringComparison.Ordinal)));

        var call = Assert.Single(result.Hunks, h => h.Path == "src/Checkout.cs");
        Assert.Equal(ChangeClass.BehaviourChange, call.Class);
    }

    [Fact]
    public void NearMissCallSiteWithAnotherEditIsNotRipple()
    {
        var result = TriageHarness.Run(
            new TestFile("src/Pricing.cs", Pricing, Pricing.Replace("int qty, int unit)", "int qty, int unit, int discount)", StringComparison.Ordinal)),
            new TestFile("src/Checkout.cs", Checkout, Checkout.Replace("Quote(qty, 250)", "Quote(qty, 250, 0)", StringComparison.Ordinal).Replace("+ 1;", "+ 2;", StringComparison.Ordinal)));

        Assert.All(result.Hunks.Where(h => h.Path == "src/Checkout.cs"), h => Assert.NotEqual(ChangeClass.Ripple, h.Class));
    }

    [Fact]
    public void ArgumentAddedToACallOfAMethodThePullRequestDoesNotChangeIsNotRipple()
    {
        var result = TriageHarness.Run(
            new TestFile("src/Checkout.cs", Checkout, Checkout.Replace("Quote(qty, 250)", "Quote(qty, 250, 0)", StringComparison.Ordinal)));

        Assert.Equal(ChangeClass.BehaviourChange, Assert.Single(result.Hunks).Class);
    }

    [Fact]
    public void NoRippleTierIsEverSkip()
    {
        var result = TriageHarness.Run(
            new TestFile("src/Pricing.cs", Pricing, Pricing.Replace("int qty, int unit)", "int qty, int unit, int discount)", StringComparison.Ordinal)),
            new TestFile("src/Checkout.cs", Checkout, Checkout.Replace("Quote(qty, 250)", "Quote(qty, 250, 0)", StringComparison.Ordinal)));

        Assert.DoesNotContain(result.Hunks, h => h.Class == ChangeClass.Ripple && h.Tier == TriageTier.Skip);
    }
}
