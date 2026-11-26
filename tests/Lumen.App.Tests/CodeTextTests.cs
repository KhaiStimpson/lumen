using Lumen.App.Views;

namespace Lumen.App.Tests;

/// <summary>Reasons name code in backticks; the app shows those names in the code font, without the backticks.</summary>
public sealed class CodeTextTests
{
    [Fact]
    public void BacktickedNamesBecomeCodeSegments()
    {
        Assert.Equal(
            [("EditModel", true), (" injects ", false), ("IOrderService", true)],
            CodeText.Segments("`EditModel` injects `IOrderService`"));
    }

    [Fact]
    public void AnUnmatchedBacktickStaysPlainText()
    {
        Assert.Equal([("a ", false), ("b", true), (" c ", false), ("`d", false)], CodeText.Segments("a `b` c `d"));
        Assert.Empty(CodeText.Segments(null));
    }
}
