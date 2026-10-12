using Lumen.Domain;
using Lumen.Models.Cloudflare;
using Lumen.Models.Walkthrough;

namespace Lumen.Models.Tests;

/// <summary>
/// One real walkthrough of a synthetic diff on Cloudflare Workers AI (opt in: <c>LUMEN_LIVE_GLM=1</c>, with
/// <c>LUMEN_CLOUDFLARE_ACCOUNT_ID</c> and <c>LUMEN_CLOUDFLARE_API_TOKEN</c> set). A handful of requests; no repository
/// code is sent.
/// </summary>
public sealed class LiveGlmTests
{
    [Fact]
    public async Task WalkthroughOfASyntheticDiff()
    {
        if (Environment.GetEnvironmentVariable("LUMEN_LIVE_GLM") != "1")
        {
            return;
        }

        using var http = new HttpClient();
        var provider = new CloudflareWorkersAiProvider(
            http,
            new MemorySecrets(),
            () => new CloudflareOptions { Model = Environment.GetEnvironmentVariable("LUMEN_GLM_MODEL") ?? new CloudflareOptions().Model },
            new UnlimitedUsageMeter());
        Assert.True((await provider.CheckAsync(CancellationToken.None)).Valid);

        var files = new[]
        {
            Files.File("src/Cache.cs", ["public sealed class Cache", "{", "    private static Dictionary<string, string> _items = new();", "    public void Set(string k, string v) => _items[k] = v;", "}"]),
            Files.File("src/CacheTests.cs", ["public class CacheTests { }"]),
        };

        var result = await new WalkthroughGenerator(provider, new WalkthroughOptions()).RunAsync(
            new WalkthroughInput { Title = "Add a cache", Description = "Adds a thread-safe cache.", Files = files, HeadSha = "head" },
            _ => { },
            CancellationToken.None);

        Assert.Equal(WalkthroughStatus.Complete, result.Status);
        Assert.NotEmpty(result.Intent);
        Assert.NotEmpty(result.Groups);
    }
}
