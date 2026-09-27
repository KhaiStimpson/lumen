using Lumen.Domain;

namespace Lumen.Repository.Tests;

public sealed class GitRepositoryWorkspaceTests : IAsyncLifetime
{
    private TestRemote remote = null!;

    public async Task InitializeAsync() => remote = await TestRemote.CreateAsync();

    public Task DisposeAsync()
    {
        remote.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CheckoutReturnsShasMergeBaseAndHeadWorktree()
    {
        var progress = new RecordingProgress();
        var workspace = new GitRepositoryWorkspace(remote.Options());

        var checkout = await workspace.CheckoutAsync(TestRemote.Key, remote.BaseSha, remote.HeadSha, progress, CancellationToken.None);

        Assert.Equal(remote.BaseSha, checkout.BaseSha);
        Assert.Equal(remote.HeadSha, checkout.HeadSha);
        Assert.Equal(remote.ForkPointSha, checkout.MergeBaseSha);
        Assert.NotEqual(checkout.BaseSha, checkout.MergeBaseSha);

        var expectedRoot = Path.Combine(remote.CacheRoot, "worktrees", "octo-widgets", remote.HeadSha[..12]);
        Assert.Equal(Path.GetFullPath(expectedRoot), checkout.RootPath);
        Assert.True(File.Exists(Path.Combine(checkout.RootPath, "src", "Added.cs")));
        Assert.False(File.Exists(Path.Combine(checkout.RootPath, "src", "Old.cs")));
        Assert.Equal("# Widgets\n", await File.ReadAllTextAsync(Path.Combine(checkout.RootPath, "README.md")));
        var config = await File.ReadAllTextAsync(Path.Combine(remote.CacheRoot, "repos", "octo", "widgets.git", "config"));
        Assert.Contains("partialclonefilter = blob:none", config, StringComparison.Ordinal);

        Assert.Equal(["Cloning octo/widgets…", "Fetching pull request…", "Checking out head…"], progress.Messages);
    }

    [Fact]
    public async Task DiffIsBetweenMergeBaseAndHead()
    {
        var checkout = await CheckoutAsync();

        var diff = await checkout.GetDiffAsync(CancellationToken.None);

        Assert.Contains("diff --git a/src/Widget.cs b/src/Widget.cs", diff, StringComparison.Ordinal);
        Assert.Contains("@@ -1,4 +1,4 @@", diff, StringComparison.Ordinal);
        Assert.Contains("-    int Size => 1;\n+    int Size => 2;", diff, StringComparison.Ordinal);
        Assert.Contains("new file mode 100644", diff, StringComparison.Ordinal);
        Assert.Contains("+++ b/src/Added.cs", diff, StringComparison.Ordinal);
        Assert.Contains("deleted file mode 100644", diff, StringComparison.Ordinal);
        Assert.Contains("--- a/src/Old.cs", diff, StringComparison.Ordinal);
        Assert.Contains("+line 20000 changed", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("README.md", diff, StringComparison.Ordinal);
        Assert.Equal(TestRemote.BigFileLines / 1000, diff.Split('\n').Count(l => l.StartsWith("+line ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ReadBaseFileReadsMergeBaseContent()
    {
        var checkout = await CheckoutAsync();

        Assert.Equal("class Widget\n{\n    int Size => 1;\n}\n", await checkout.ReadBaseFileAsync("src/Widget.cs", CancellationToken.None));
        Assert.Equal("class Old { }\n", await checkout.ReadBaseFileAsync("src/Old.cs", CancellationToken.None));
        Assert.Equal("# Widgets\n", await checkout.ReadBaseFileAsync("README.md", CancellationToken.None));
        Assert.Equal(TestRemote.BigFileBase, await checkout.ReadBaseFileAsync("data/big.txt", CancellationToken.None));
        Assert.Null(await checkout.ReadBaseFileAsync("src/Added.cs", CancellationToken.None));
        Assert.Null(await checkout.ReadBaseFileAsync("src", CancellationToken.None));
        Assert.Null(await checkout.ReadBaseFileAsync("nope/missing.txt", CancellationToken.None));
    }

    [Fact]
    public async Task ReadHeadFileReadsWorktreeAndRejectsTraversal()
    {
        var checkout = await CheckoutAsync();

        Assert.Equal("class Widget\n{\n    int Size => 2;\n}\n", await checkout.ReadHeadFileAsync("src/Widget.cs", CancellationToken.None));
        Assert.Equal("class Added { }\n", await checkout.ReadHeadFileAsync("src\\Added.cs", CancellationToken.None));
        Assert.Equal(TestRemote.BigFileHead, await checkout.ReadHeadFileAsync("data/big.txt", CancellationToken.None));
        Assert.Null(await checkout.ReadHeadFileAsync("src/Old.cs", CancellationToken.None));
        Assert.Null(await checkout.ReadHeadFileAsync("src", CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() => checkout.ReadHeadFileAsync("../../repos/octo/widgets.git/config", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => checkout.ReadHeadFileAsync("src/../../x", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => checkout.ReadHeadFileAsync(Path.Combine(remote.RepoPath, "README.md"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => checkout.ReadHeadFileAsync("/etc/passwd", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => checkout.ReadHeadFileAsync(".git", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => checkout.ReadBaseFileAsync("../x", CancellationToken.None));
    }

    [Fact]
    public async Task SecondCheckoutReusesCloneAndWorktree()
    {
        var first = await CheckoutAsync();
        var marker = Path.Combine(first.RootPath, "marker.tmp");
        await File.WriteAllTextAsync(marker, "still here");

        var progress = new RecordingProgress();
        var workspace = new GitRepositoryWorkspace(remote.Options());
        var second = await workspace.CheckoutAsync(TestRemote.Key, remote.BaseSha, remote.HeadSha, progress, CancellationToken.None);

        Assert.Empty(progress.Messages);
        Assert.Equal(first.RootPath, second.RootPath);
        Assert.Equal(first.MergeBaseSha, second.MergeBaseSha);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task NewHeadIsFetchedIntoNewWorktree()
    {
        var workspace = new GitRepositoryWorkspace(remote.Options());
        var first = await workspace.CheckoutAsync(TestRemote.Key, remote.BaseSha, remote.HeadSha, null, CancellationToken.None);
        var newHead = await remote.AddFeatureCommitAsync("src/Later.cs", "class Later { }\n");

        var progress = new RecordingProgress();
        var second = await workspace.CheckoutAsync(TestRemote.Key, remote.BaseSha, newHead, progress, CancellationToken.None);

        Assert.Equal(["Fetching pull request…", "Checking out head…"], progress.Messages);
        Assert.NotEqual(first.RootPath, second.RootPath);
        Assert.Equal("class Later { }\n", await second.ReadHeadFileAsync("src/Later.cs", CancellationToken.None));
        Assert.Null(await first.ReadHeadFileAsync("src/Later.cs", CancellationToken.None));
        Assert.Contains("+++ b/src/Later.cs", await second.GetDiffAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrokenWorktreeDirectoryIsRecreated()
    {
        var first = await CheckoutAsync();
        GitRepositoryWorkspace.DeleteDirectory(first.RootPath);
        Directory.CreateDirectory(first.RootPath);
        await File.WriteAllTextAsync(Path.Combine(first.RootPath, "junk.txt"), "junk");

        var second = await CheckoutAsync();

        Assert.False(File.Exists(Path.Combine(second.RootPath, "junk.txt")));
        Assert.True(File.Exists(Path.Combine(second.RootPath, "src", "Added.cs")));
    }

    [Fact]
    public async Task ConcurrentCheckoutsOfSameRepositoryDoNotRace()
    {
        var workspace = new GitRepositoryWorkspace(remote.Options());

        var checkouts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            workspace.CheckoutAsync(TestRemote.Key, remote.BaseSha, remote.HeadSha, null, CancellationToken.None)));

        Assert.All(checkouts, c => Assert.Equal(checkouts[0].RootPath, c.RootPath));
        Assert.Equal("class Added { }\n", await checkouts[3].ReadHeadFileAsync("src/Added.cs", CancellationToken.None));
    }

    [Fact]
    public async Task TokenProviderDoesNotAffectNonGitHubRemotes()
    {
        var calls = 0;
        var workspace = new GitRepositoryWorkspace(remote.Options(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>("secret-token");
        }));

        var checkout = await workspace.CheckoutAsync(TestRemote.Key, remote.BaseSha, remote.HeadSha, null, CancellationToken.None);

        Assert.True(calls > 0);
        var config = await File.ReadAllTextAsync(Path.Combine(remote.CacheRoot, "repos", "octo", "widgets.git", "config"));
        Assert.DoesNotContain("secret-token", config, StringComparison.Ordinal);
        Assert.DoesNotContain("extraheader", config, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await checkout.ReadBaseFileAsync("src/Widget.cs", CancellationToken.None));
    }

    [Fact]
    public async Task UnknownCommitFailsWithClearError()
    {
        var workspace = new GitRepositoryWorkspace(remote.Options());
        var missing = new string('a', 40);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.CheckoutAsync(TestRemote.Key, remote.BaseSha, missing, null, CancellationToken.None));

        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--upload-pack=evil")]
    [InlineData("HEAD")]
    [InlineData("abc123")]
    public async Task InvalidShaIsRejected(string sha)
    {
        var workspace = new GitRepositoryWorkspace(remote.Options());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            workspace.CheckoutAsync(TestRemote.Key, remote.BaseSha, sha, null, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidRepositoryNameIsRejected()
    {
        var workspace = new GitRepositoryWorkspace(remote.Options());
        var key = new PullRequestKey(new RepositoryRef("..", "widgets"), 7);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            workspace.CheckoutAsync(key, remote.BaseSha, remote.HeadSha, null, CancellationToken.None));
    }

    private Task<IPullRequestCheckout> CheckoutAsync() =>
        new GitRepositoryWorkspace(remote.Options())
            .CheckoutAsync(TestRemote.Key, remote.BaseSha, remote.HeadSha, null, CancellationToken.None);

    private sealed class RecordingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value)
        {
            lock (Messages)
            {
                Messages.Add(value);
            }
        }
    }
}
