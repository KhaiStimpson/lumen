using Lumen.Analysis.Tests.Support;
using Lumen.Domain;

namespace Lumen.Analysis.Tests.Triage;

public class CommentsAndImportsClassifierTests
{
    private const string Base = """
        using System;
        using System.Linq;

        namespace Shop;

        public class Order
        {
            // Sums the lines.
            public int Total(int[] lines) => lines.Sum();
        }
        """;

    [Fact]
    public void ChangedLineCommentIsCommentsOnly()
    {
        var hunk = TriageHarness.Single(Base, Base.Replace("// Sums the lines.", "// Sums every order line.", StringComparison.Ordinal));

        Assert.Equal(ChangeClass.CommentsOnly, hunk.Class);
        Assert.Equal(TriageTier.Skip, hunk.Tier);
        Assert.Equal(["comments only"], hunk.Reasons);
    }

    [Fact]
    public void AddedDocCommentIsCommentsOnly()
    {
        var head = Base.Replace("    // Sums the lines.\n", "    // Sums the lines.\n    /// <summary>The order total.</summary>\n", StringComparison.Ordinal);

        var hunk = TriageHarness.Single(Base, head);

        Assert.Equal(ChangeClass.CommentsOnly, hunk.Class);
        Assert.Equal(["comments and doc comments only"], hunk.Reasons);
    }

    [Fact]
    public void NearMissCommentPlusCodeChangeIsNotCommentsOnly()
    {
        var head = Base
            .Replace("// Sums the lines.", "// Sums the lines twice.", StringComparison.Ordinal)
            .Replace("lines.Sum();", "lines.Sum() * 2;", StringComparison.Ordinal);

        Assert.Equal(ChangeClass.BehaviourChange, TriageHarness.Single(Base, head).Class);
    }

    [Fact]
    public void CommentingOutCodeIsNotCommentsOnly()
    {
        var head = Base.Replace("    public int Total", "    // public int Total", StringComparison.Ordinal);

        Assert.Equal(ChangeClass.BehaviourChange, TriageHarness.Single(Base, head).Class);
    }

    [Fact]
    public void AddedUsingIsImportsOnly()
    {
        var hunk = TriageHarness.Single(Base, Base.Replace("using System.Linq;", "using System.Linq;\nusing System.Text;", StringComparison.Ordinal));

        Assert.Equal(ChangeClass.ImportsOnly, hunk.Class);
        Assert.Equal(TriageTier.Skip, hunk.Tier);
        Assert.Equal(["usings added: System.Text"], hunk.Reasons);
    }

    [Fact]
    public void RemovedUsingIsImportsOnly()
    {
        var hunk = TriageHarness.Single(Base, Base.Replace("using System;\n", "", StringComparison.Ordinal));

        Assert.Equal(ChangeClass.ImportsOnly, hunk.Class);
        Assert.Equal(["usings removed: System"], hunk.Reasons);
    }

    [Fact]
    public void ReorderedUsingsAreImportsOnly()
    {
        var head = Base.Replace("using System;\nusing System.Linq;", "using System.Linq;\nusing System;", StringComparison.Ordinal);

        var hunk = TriageHarness.Single(Base, head);

        Assert.Equal(ChangeClass.ImportsOnly, hunk.Class);
        Assert.Equal(["usings reordered"], hunk.Reasons);
    }

    [Fact]
    public void NearMissUsingPlusCodeChangeIsNotImportsOnly()
    {
        const string before = "using System;\nclass C\n{\n    int X => 1;\n}\n";
        const string after = "using System;\nusing System.Text;\nclass C\n{\n    int X => 2;\n}\n";

        Assert.Equal(ChangeClass.BehaviourChange, TriageHarness.Single(before, after).Class);
    }

    [Theory]
    [InlineData("using Json = System.Text.Json.JsonSerializer;")]
    [InlineData("using static System.Math;")]
    [InlineData("global using System.Text;")]
    public void AliasStaticAndGlobalUsingsAreNotImportsOnly(string directive)
    {
        var hunk = TriageHarness.Single(Base, Base.Replace("using System.Linq;", "using System.Linq;\n" + directive, StringComparison.Ordinal));

        Assert.Equal(ChangeClass.BehaviourChange, hunk.Class);
    }
}
