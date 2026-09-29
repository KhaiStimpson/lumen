using Lumen.Analysis.Tests.Support;
using Lumen.Domain;

namespace Lumen.Analysis.Tests.Triage;

public class FormattingClassifierTests
{
    private const string Base = """
        namespace Shop;

        public class Order
        {
            public int Total(int a, int b)
            {
                var sum = a + b;
                return sum * 2;
            }

            public string Label => "total: " + Total(1, 2);
        }
        """;

    [Fact]
    public void ReindentedAndReflowedCodeIsFormatting()
    {
        var head = Base
            .Replace("var sum = a + b;", "var sum =\n                a + b;", StringComparison.Ordinal)
            .Replace("return sum * 2;", "return sum*2;", StringComparison.Ordinal);

        var hunk = TriageHarness.Single(Base, head);

        Assert.Equal(ChangeClass.Formatting, hunk.Class);
        Assert.Equal(TriageTier.Skip, hunk.Tier);
        Assert.Equal(["formatting only"], hunk.Reasons);
    }

    [Fact]
    public void AddedBlankLineIsFormatting()
    {
        var head = Base.Replace("        return sum * 2;", "\n        return sum * 2;", StringComparison.Ordinal);

        Assert.Equal(ChangeClass.Formatting, TriageHarness.Single(Base, head).Class);
    }

    [Fact]
    public void NearMissOneChangedLiteralIsNotFormatting()
    {
        var head = Base
            .Replace("var sum = a + b;", "var sum =  a + b;", StringComparison.Ordinal)
            .Replace("return sum * 2;", "return sum * 3;", StringComparison.Ordinal);

        var hunk = TriageHarness.Single(Base, head);

        Assert.Equal(ChangeClass.BehaviourChange, hunk.Class);
        Assert.NotEqual(TriageTier.Skip, hunk.Tier);
    }

    [Fact]
    public void NearMissReorderedTokensIsNotFormatting()
    {
        var head = Base.Replace("var sum = a + b;", "var sum = b + a;", StringComparison.Ordinal);

        Assert.Equal(ChangeClass.BehaviourChange, TriageHarness.Single(Base, head).Class);
    }

    [Fact]
    public void WhitespaceInsideAStringIsNotFormatting()
    {
        var head = Base.Replace("\"total: \"", "\"total:  \"", StringComparison.Ordinal);

        Assert.Equal(ChangeClass.BehaviourChange, TriageHarness.Single(Base, head).Class);
    }

    [Fact]
    public void WhitespaceChangeInsideAMultiLineRawStringIsNotFormatting()
    {
        const string before = "class Q\n{\n    const string Sql = \"\"\"\n        select *\n        from orders\n        \"\"\";\n}\n";
        var after = before.Replace("        from orders", "          from orders", StringComparison.Ordinal);

        Assert.Equal(ChangeClass.BehaviourChange, TriageHarness.Single(before, after).Class);
    }

    [Fact]
    public void ChangedCommentIsNotFormatting()
    {
        const string before = "class C\n{\n    // keeps totals\n    int X;\n}\n";
        var after = before.Replace("keeps totals", "keeps grand totals", StringComparison.Ordinal);

        Assert.NotEqual(ChangeClass.Formatting, TriageHarness.Single(before, after).Class);
    }

    [Fact]
    public void ChangedPreprocessorConditionIsNotFormatting()
    {
        const string before = "class C\n{\n#if DEBUG\n    int X;\n#endif\n}\n";
        var after = before.Replace("#if DEBUG", "#if  RELEASE", StringComparison.Ordinal);

        Assert.Equal(ChangeClass.BehaviourChange, TriageHarness.Single(before, after).Class);
    }

    [Fact]
    public void OpeningACommentThatSwallowsCodeIsNotFormatting()
    {
        const string before = "class C\n{\n    int X; /* note */\n    int Y;\n    int Z; /* end */\n}\n";
        var after = before.Replace("int X; /* note */", "int X; /* note", StringComparison.Ordinal);

        Assert.Equal(ChangeClass.BehaviourChange, TriageHarness.Single(before, after).Class);
    }

    [Fact]
    public void FileThatDoesNotParseProvesNothing()
    {
        const string before = "class C {\n    int X;\n";
        const string after = "class C {\n        int X;\n";

        Assert.Equal(ChangeClass.BehaviourChange, TriageHarness.Single(before, after).Class);
    }

    [Fact]
    public void WithoutSourcesNothingIsProven()
    {
        var head = Base.Replace("return sum * 2;", "return sum*2;", StringComparison.Ordinal);
        var snapshot = TestDiffs.Snapshot(TriageHarness.Diff("src/A.cs", Base, head));

        var hunk = Assert.Single(Roslyn.Triage.RoslynTriage.CreatePipeline().Run(snapshot).Hunks);

        Assert.Equal(ChangeClass.BehaviourChange, hunk.Class);
    }

    [Fact]
    public void OnlyTheFormattingHunkIsSkippedWhenAnotherHunkChangesBehaviour()
    {
        const string before = "class C\n{\n    int A() => 1;\n\n\n\n\n\n\n\n\n\n    int B() => 2;\n}\n";
        var after = before.Replace("int A() => 1;", "int A()  =>  1;", StringComparison.Ordinal).Replace("=> 2;", "=> 3;", StringComparison.Ordinal);

        var hunks = TriageHarness.Run(new TestFile("src/C.cs", before, after)).Hunks;

        Assert.Equal(2, hunks.Count);
        Assert.Equal(ChangeClass.Formatting, hunks[0].Class);
        Assert.Equal(ChangeClass.BehaviourChange, hunks[1].Class);
    }

    [Fact]
    public void NonCSharpFilesAreNotClassified()
    {
        var hunk = TriageHarness.Single("a: 1\nb: 2\n", "a: 1\n  b: 2\n", "config.yaml");

        Assert.Equal(ChangeClass.BehaviourChange, hunk.Class);
    }
}
