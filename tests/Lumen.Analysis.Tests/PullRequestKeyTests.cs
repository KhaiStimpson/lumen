using Lumen.Domain;

namespace Lumen.Analysis.Tests;

public sealed class PullRequestKeyTests
{
    [Theory]
    [InlineData("https://github.com/acme/shop/pull/12")]
    [InlineData("https://github.com/acme/shop/pull/12/files")]
    [InlineData("https://github.com/acme/shop/pull/12?diff=split#discussion_r1")]
    [InlineData("http://www.github.com/acme/shop/pull/12")]
    [InlineData("acme/shop#12")]
    [InlineData("  acme/shop#12  ")]
    [InlineData("acme/shop/pull/12")]
    [InlineData("acme/shop/PULL/12")]
    public void ParsesSupportedForms(string text)
    {
        Assert.True(PullRequestKey.TryParse(text, out var key));
        Assert.Equal(new PullRequestKey(new RepositoryRef("acme", "shop"), 12), key);
        Assert.Equal("acme/shop#12", key.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("acme/shop")]
    [InlineData("acme#12")]
    [InlineData("#12")]
    [InlineData("acme/shop/extra#12")]
    [InlineData("acme/shop#")]
    [InlineData("acme/shop#abc")]
    [InlineData("acme/shop#0")]
    [InlineData("acme/shop#-3")]
    [InlineData("acme/shop/issues/12")]
    [InlineData("acme/shop/pull")]
    [InlineData("acme/shop/pull/x")]
    [InlineData("https://gitlab.com/acme/shop/pull/12")]
    [InlineData("https://github.com/acme/shop/issues/12")]
    [InlineData("https://github.com/acme/shop")]
    public void RejectsInvalidInput(string? text)
    {
        Assert.False(PullRequestKey.TryParse(text, out _));
    }

    [Fact]
    public void RejectsLookAlikeHosts()
    {
        Assert.False(PullRequestKey.TryParse("https://notgithub.com/acme/shop/pull/12", out _));
    }
}
