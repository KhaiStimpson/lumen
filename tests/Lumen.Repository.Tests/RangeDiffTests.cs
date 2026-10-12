namespace Lumen.Repository.Tests;

/// <summary>"Since last review" and commit ranges (plan v2 M2) against a real local repository.</summary>
public sealed class RangeDiffTests : IAsyncLifetime
{
    private TestRemote remote = null!;

    public async Task InitializeAsync() => remote = await TestRemote.CreateAsync();

    public Task DisposeAsync()
    {
        remote.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RangeDiffShowsOnlyTheNewCommitsChanges()
    {
        var reviewed = remote.HeadSha;
        var head = await remote.AddFeatureCommitAsync("src/Widget.cs", "class Widget\n{\n    int Size => 3;\n}\n");
        var checkout = await new GitRepositoryWorkspace(remote.Options()).CheckoutAsync(TestRemote.Key, remote.BaseSha, head, null, CancellationToken.None);

        var diff = await checkout.GetRangeDiffAsync(reviewed, head, false, null, CancellationToken.None);

        Assert.Contains("-    int Size => 2;\n+    int Size => 3;", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("Added.cs", diff, StringComparison.Ordinal);
        Assert.True(await checkout.IsAncestorAsync(reviewed, head, CancellationToken.None));

        var onlyWidget = await checkout.GetRangeDiffAsync(checkout.MergeBaseSha, head, false, "src/Widget.cs", CancellationToken.None);
        Assert.DoesNotContain("Added.cs", onlyWidget, StringComparison.Ordinal);
        Assert.Contains("+    int Size => 3;", onlyWidget, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhitespaceOnlyChangesDisappearWithIgnoreWhitespace()
    {
        var reviewed = remote.HeadSha;
        var head = await remote.AddFeatureCommitAsync("src/Widget.cs", "class Widget\n{\n        int Size => 2;\n}\n");
        var checkout = await new GitRepositoryWorkspace(remote.Options()).CheckoutAsync(TestRemote.Key, remote.BaseSha, head, null, CancellationToken.None);

        Assert.Contains("Size", await checkout.GetRangeDiffAsync(reviewed, head, false, null, CancellationToken.None), StringComparison.Ordinal);
        Assert.Equal("", (await checkout.GetRangeDiffAsync(reviewed, head, true, null, CancellationToken.None)).Trim());
    }

    [Fact]
    public async Task AForcePushedAwayHeadIsNotAnAncestorButCanStillBeCompared()
    {
        var reviewed = remote.HeadSha;

        // Rewrite history: a new commit on the fork point replaces the reviewed head.
        await remote.RunAsync("checkout", "-q", "--detach", remote.ForkPointSha);
        await File.WriteAllTextAsync(Path.Combine(remote.RepoPath, "src", "Widget.cs"), "class Widget\n{\n    int Size => 5;\n}\n");
        await remote.RunAsync("commit", "-q", "-am", "rewritten");
        var rewritten = (await remote.RunAsync("rev-parse", "HEAD")).StandardOutput.Trim();
        await remote.RunAsync("update-ref", $"refs/pull/{TestRemote.PullNumber}/head", rewritten);
        await remote.RunAsync("checkout", "-q", "main");

        var workspace = new GitRepositoryWorkspace(remote.Options());
        var checkout = await workspace.CheckoutAsync(TestRemote.Key, remote.BaseSha, rewritten, null, CancellationToken.None);

        Assert.True(await checkout.EnsureCommitAsync(reviewed, CancellationToken.None));
        Assert.False(await checkout.IsAncestorAsync(reviewed, rewritten, CancellationToken.None));
        var diff = await checkout.GetRangeDiffAsync(reviewed, rewritten, false, "src/Widget.cs", CancellationToken.None);
        Assert.Contains("-    int Size => 2;\n+    int Size => 5;", diff, StringComparison.Ordinal);

        Assert.False(await checkout.EnsureCommitAsync(new string('e', 40), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => checkout.GetRangeDiffAsync("HEAD~1", rewritten, false, null, CancellationToken.None));
    }
}
