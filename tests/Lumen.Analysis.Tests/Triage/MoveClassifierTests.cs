using Lumen.Analysis.Tests.Support;
using Lumen.Domain;

namespace Lumen.Analysis.Tests.Triage;

public class MoveClassifierTests
{
    private const string Header = "using System;\n\nnamespace Shop;\n\n";

    private const string Round = """
            private static int Round(int value)
            {
                var cents = value / 100;
                return cents * 100;
            }
        """;

    private const string Price = """
            public int Price(int qty)
            {
                return Round(qty * 250);
            }
        """;

    private const string Tax = """
            public int Tax(int amount)
            {
                return amount / 10;
            }
        """;

    private static string Class(string name, params string[] members) =>
        Header + $"public partial class {name}\n{{\n" + string.Join("\n\n", members) + "\n}\n";

    [Fact]
    public void MethodMovedWithinFileIsMoveAndGrouped()
    {
        var before = Class("Order", Round, Price, Tax);
        var after = Class("Order", Price, Tax, Round);

        var result = TriageHarness.Run(new TestFile("src/Order.cs", before, after));

        Assert.All(result.Hunks, h => Assert.Equal((ChangeClass.Move, TriageTier.Skip), (h.Class, h.Tier)));
        var group = Assert.Single(result.Groups);
        Assert.Equal("Moved `Round` within Order.cs", group.Title);
        Assert.Equal(2, group.Members.Count);
    }

    [Fact]
    public void MethodMovedBetweenPartialFilesIsSkip()
    {
        var result = TriageHarness.Run(
            new TestFile("src/Order.cs", Class("Order", Round, Price), Class("Order", Price)),
            new TestFile("src/Order.Rounding.cs", Class("Order", Tax), Class("Order", Tax, Round)));

        Assert.All(result.Hunks, h => Assert.Equal((ChangeClass.Move, TriageTier.Skip), (h.Class, h.Tier)));
        var group = Assert.Single(result.Groups);
        Assert.Equal("Moved `Round` from Order.cs to Order.Rounding.cs", group.Title);
        Assert.Equal(["src/Order.Rounding.cs", "src/Order.cs"], group.Members.Select(m => m.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void MethodMovedIntoAnotherClassIsSkimNotSkip()
    {
        var result = TriageHarness.Run(
            new TestFile("src/Order.cs", Class("Order", Round, Price), Class("Order", Price)),
            new TestFile("src/Money.cs", Class("Money", Tax), Class("Money", Tax, Round)));

        Assert.All(result.Hunks, h => Assert.Equal((ChangeClass.Move, TriageTier.Skim), (h.Class, h.Tier)));
        Assert.Contains(result.Hunks[0].Reasons, r => r.Contains("bind differently", StringComparison.Ordinal));
    }

    [Fact]
    public void TypeMovedToANewFileIsSkip()
    {
        const string helper = "public static class Rounding\n{\n    public static int Up(int v) => v + 1;\n}\n";
        var before = Header + "public class Order\n{\n}\n\n" + helper;
        var after = Header + "public class Order\n{\n}\n";

        var result = TriageHarness.Run(
            new TestFile("src/Order.cs", before, after),
            new TestFile("src/Rounding.cs", null, Header + helper));

        var group = Assert.Single(result.Groups);
        Assert.Equal("Moved `Rounding` from Order.cs to Rounding.cs", group.Title);
        Assert.All(group.Members, h => Assert.Equal((ChangeClass.Move, TriageTier.Skip), (h.Class, h.Tier)));

        // The new file's `using`/`namespace` header is not part of the move: it is new code.
        var rest = Assert.Single(result.Hunks, h => h.GroupId is null);
        Assert.Equal((ChangeClass.NewCode, "src/Rounding.cs", 1, 3), (rest.Class, rest.Path, rest.NewStart, rest.NewEnd));
    }

    [Fact]
    public void NearMissMoveThatAlsoChangesOneLineFlagsOnlyThatLine()
    {
        var edited = Round.Replace("return cents * 100;", "return cents * 1000;", StringComparison.Ordinal);
        var result = TriageHarness.Run(
            new TestFile("src/Order.cs", Class("Order", Round, Price), Class("Order", Price)),
            new TestFile("src/Order.Rounding.cs", Class("Order", Tax), Class("Order", Tax, edited)));

        var behaviour = result.Hunks.Where(h => h.Class == ChangeClass.BehaviourChange).ToList();
        var changedLine = TestDiffs.LineOf(Class("Order", Tax, edited), "cents * 1000");
        var flagged = Assert.Single(behaviour, h => h.Path == "src/Order.Rounding.cs");
        Assert.Equal((changedLine, changedLine), (flagged.NewStart, flagged.NewEnd));
        Assert.Equal(["edited while moving `Round`"], flagged.Reasons);
        Assert.Contains(result.Hunks, h => h.Path == "src/Order.Rounding.cs" && h.Class == ChangeClass.Move);
        Assert.DoesNotContain(result.Hunks, h => h.Class == ChangeClass.Move && h.NewStart <= changedLine && h.NewEnd >= changedLine);
    }

    [Fact]
    public void ReorderedEnumMembersAreNotMoves()
    {
        const string before = "enum Status\n{\n    Open,\n    Closed,\n    Archived,\n}\n";
        const string after = "enum Status\n{\n    Closed,\n    Open,\n    Archived,\n}\n";

        Assert.All(TriageHarness.Run(new TestFile("src/Status.cs", before, after)).Hunks, h => Assert.NotEqual(ChangeClass.Move, h.Class));
    }

    [Fact]
    public void MovedFieldWithInitialiserIsSkim()
    {
        const string a = "    private static readonly int A = 5;";
        const string b = "    private static readonly int B = A + 1;";
        var before = Header + "public class C\n{\n" + a + "\n\n" + b + "\n}\n";
        var after = Header + "public class C\n{\n" + b + "\n\n" + a + "\n}\n";

        var hunks = TriageHarness.Run(new TestFile("src/C.cs", before, after)).Hunks;

        Assert.All(hunks, h => Assert.NotEqual(TriageTier.Skip, h.Tier));
    }

    [Fact]
    public void HunkWithAMoveAndAnUnrelatedEditIsSplit()
    {
        var before = Class("Order", Round, Price);
        var after = Class("Order", Price.Replace("qty * 250", "qty * 260", StringComparison.Ordinal), Round);

        var hunks = TriageHarness.Run(new TestFile("src/Order.cs", before, after)).Hunks;

        // `Price` is the member the diff shows as moved (it is shorter than `Round`), edited on one line.
        Assert.Contains(hunks, h => h.Class == ChangeClass.Move && h.Tier == TriageTier.Skip);
        var edits = hunks.Where(h => h.Class == ChangeClass.BehaviourChange).ToList();
        Assert.Equal(2, edits.Sum(h => h.ChangedLines));
        Assert.All(edits, h => Assert.Equal(["edited while moving `Price`"], h.Reasons));
        Assert.Equal(hunks.Sum(h => h.ChangedLines), TriageHarness.Run(TriagePipeline.Default, new TestFile("src/Order.cs", before, after)).Hunks.Sum(h => h.ChangedLines));
    }

    [Fact]
    public void MoveIntoAConditionalRegionIsNotAMove()
    {
        var before = Class("Order", Round, Price) + "\n#if DEBUG\npublic class Diagnostics\n{\n}\n#endif\n";
        var after = Class("Order", Price) + "\n#if DEBUG\npublic class Diagnostics\n{\n}\n#endif\n";
        var target = Header + "public partial class Order\n{\n#if !RELEASE\n" + Round + "\n#endif\n}\n";

        var result = TriageHarness.Run(
            new TestFile("src/Order.cs", before, after),
            new TestFile("src/Order.Debug.cs", null, target));

        Assert.DoesNotContain(result.Hunks, h => h.Tier == TriageTier.Skip);
    }

    [Fact]
    public void MemberSharingALineWithAnotherIsNotAMove()
    {
        const string before = "class C\n{\n    int A() => 1; int B() => 2;\n\n    int D() => 4;\n}\n";
        const string after = "class C\n{\n    int B() => 3;\n\n    int D() => 4;\n    int A() => 1;\n}\n";

        var hunks = TriageHarness.Run(new TestFile("src/C.cs", before, after)).Hunks;

        var changedB = TestDiffs.LineOf(after, "B() => 3");
        Assert.DoesNotContain(hunks, h => h.Class == ChangeClass.Move && h.NewStart <= changedB && h.NewEnd >= changedB);
        Assert.DoesNotContain(hunks, h => h.Class == ChangeClass.Move && h.OldStart <= 3 && h.OldEnd >= 3);
    }

    [Fact]
    public void DeletedMethodWithNoCounterpartIsNotAMove()
    {
        var hunks = TriageHarness.Run(new TestFile("src/Order.cs", Class("Order", Round, Price), Class("Order", Price.Replace("Round(qty * 250)", "qty * 250", StringComparison.Ordinal)))).Hunks;

        Assert.All(hunks, h => Assert.Equal(ChangeClass.BehaviourChange, h.Class));
    }
}
