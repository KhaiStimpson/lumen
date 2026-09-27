namespace Lumen.Repository.Tests;

public sealed class GitAgentWorktreeFactoryTests : IAsyncLifetime
{
    private TestRemote remote = null!;

    public async Task InitializeAsync() => remote = await TestRemote.CreateAsync();

    public Task DisposeAsync()
    {
        remote.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreatesAnIsolatedHeadWorktreeAndRemovesIt()
    {
        var checkout = await new GitRepositoryWorkspace(remote.Options())
            .CheckoutAsync(TestRemote.Key, remote.BaseSha, remote.HeadSha, null, CancellationToken.None);
        var factory = new GitAgentWorktreeFactory(Path.Combine(remote.CacheRoot, "agents"));

        var worktree = await factory.CreateAsync(checkout, "correctness-001", CancellationToken.None);
        var path = worktree.Path;
        try
        {
            Assert.StartsWith(factory.Root, path, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(path, "src", "Added.cs")));

            // Agent edits stay in its own worktree.
            await File.WriteAllTextAsync(Path.Combine(path, "src", "Added.cs"), "// agent scratch");
            Assert.NotEqual("// agent scratch", await File.ReadAllTextAsync(Path.Combine(checkout.RootPath, "src", "Added.cs")));
        }
        finally
        {
            await worktree.DisposeAsync();
        }

        Assert.False(Directory.Exists(path));
        var list = await remote.RunAsync("-C", checkout.RootPath, "worktree", "list");
        Assert.DoesNotContain("correctness-001", list.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task RejectsNamesThatCouldEscapeTheRoot(string name)
    {
        var checkout = await new GitRepositoryWorkspace(remote.Options())
            .CheckoutAsync(TestRemote.Key, remote.BaseSha, remote.HeadSha, null, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new GitAgentWorktreeFactory(Path.Combine(remote.CacheRoot, "agents")).CreateAsync(checkout, name, CancellationToken.None));
    }
}
