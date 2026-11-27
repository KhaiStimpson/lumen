using System.Security.Cryptography;
using Lumen.Domain;
using Lumen.TestSupport;

namespace Lumen.Repository.Tests;

/// <summary>Local review is read-only on the clone: the working tree, index and refs come out exactly as they went in.</summary>
public sealed class GitLocalRepositoriesTests : IDisposable
{
    private readonly FixtureRepository _repo = FixtureRepository.Create();
    private readonly string _data = Path.Combine(Path.GetTempPath(), "lumen-local-data", Guid.NewGuid().ToString("N")[..12]);

    private GitLocalRepositories Repositories => new(_data);

    public void Dispose()
    {
        _repo.Dispose();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    [Fact]
    public async Task ABranchIsReadFromCommitsWithTheRightMergeBase()
    {
        var review = await Repositories.OpenAsync(new LocalBranchTarget(_repo.Root, "main", LocalHeadKind.Branch, FixtureRepository.HeadBranch), CancellationToken.None);

        Assert.Equal(_repo.BaseSha, review.Checkout.BaseSha);
        Assert.Equal(_repo.HeadSha, review.Checkout.HeadSha);
        Assert.Equal(_repo.BaseSha, review.Checkout.MergeBaseSha);
        Assert.False(review.HasUncommittedChanges);
        Assert.Contains("diff --git a/src/Shop.Web/Program.cs b/src/Shop.Web/Program.cs", await review.Checkout.GetDiffAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABranchIsReadEvenWhenAnotherBranchIsCheckedOut()
    {
        _repo.Git("checkout", "-q", "main");
        var review = await Repositories.OpenAsync(new LocalBranchTarget(_repo.Root, "main", LocalHeadKind.Branch, FixtureRepository.HeadBranch), CancellationToken.None);

        var head = await review.Checkout.ReadHeadFileAsync("src/Shop.Web/Program.cs", CancellationToken.None);
        var onDisk = File.ReadAllText(_repo.PathOf("src/Shop.Web/Program.cs"));

        Assert.NotEqual(onDisk, head);
        Assert.Equal(File.ReadAllText(Path.Combine(FixtureRepository.FixtureDirectory, "head", "src", "Shop.Web", "Program.cs")), head);
    }

    [Fact]
    public async Task TheWorkingTreeIncludingUncommittedAndUntrackedChangesIsSnapshotted()
    {
        _repo.Write("src/Shop.Web/Uncommitted.cs", "namespace Shop.Web;\n\npublic sealed class Uncommitted { }\n");
        _repo.Write("src/Shop.Web/Program.cs", File.ReadAllText(_repo.PathOf("src/Shop.Web/Program.cs")) + "// edited, not committed\n");
        var target = new LocalBranchTarget(_repo.Root, "main", LocalHeadKind.WorkingTree);

        var review = await Repositories.OpenAsync(target, CancellationToken.None);

        Assert.True(review.HasUncommittedChanges);
        Assert.NotEqual(_repo.HeadSha, review.Checkout.HeadSha);
        Assert.Equal(_repo.BaseSha, review.Checkout.MergeBaseSha);
        var diff = await review.Checkout.GetDiffAsync(CancellationToken.None);
        Assert.Contains("+++ b/src/Shop.Web/Uncommitted.cs", diff, StringComparison.Ordinal);
        Assert.Contains("+// edited, not committed", diff, StringComparison.Ordinal);
        Assert.EndsWith("// edited, not committed\n", await review.Checkout.ReadHeadFileAsync("src/Shop.Web/Program.cs", CancellationToken.None), StringComparison.Ordinal);

        // The private index lives outside the clone and is gone afterwards.
        Assert.StartsWith(_data, Repositories.PrivateIndexPath(target), StringComparison.Ordinal);
        Assert.False(File.Exists(Repositories.PrivateIndexPath(target)));
    }

    [Fact]
    public async Task TheSameUncommittedChangesGetTheSameSnapshotHash()
    {
        _repo.Write("notes.txt", "uncommitted\n");
        var target = new LocalBranchTarget(_repo.Root, "main", LocalHeadKind.WorkingTree);

        var first = await Repositories.OpenAsync(target, CancellationToken.None);
        var second = await Repositories.OpenAsync(target, CancellationToken.None);
        _repo.Write("notes.txt", "uncommitted, then edited\n");
        var third = await Repositories.OpenAsync(target, CancellationToken.None);

        Assert.Equal(first.Checkout.HeadSha, second.Checkout.HeadSha);
        Assert.NotEqual(first.Checkout.HeadSha, third.Checkout.HeadSha);
    }

    [Fact]
    public async Task ACleanWorkingTreeIsJustTheHeadCommit()
    {
        var review = await Repositories.OpenAsync(new LocalBranchTarget(_repo.Root, "main", LocalHeadKind.WorkingTree), CancellationToken.None);

        Assert.Equal(_repo.HeadSha, review.Checkout.HeadSha);
        Assert.False(review.HasUncommittedChanges);
        Assert.Equal($"{FixtureRepository.HeadBranch} (working tree)", review.HeadLabel);
    }

    [Fact]
    public async Task TheUsersWorkingTreeIndexAndRefsAreUntouched()
    {
        // A realistic mess: a staged change, an unstaged change on top of it, an untracked file and an ignored file.
        _repo.Write(".gitignore", "*.log\n");
        _repo.Git("add", ".gitignore");
        _repo.Write("src/Shop.Web/Program.cs", File.ReadAllText(_repo.PathOf("src/Shop.Web/Program.cs")) + "// staged\n");
        _repo.Git("add", "src/Shop.Web/Program.cs");
        _repo.Write("src/Shop.Web/Program.cs", File.ReadAllText(_repo.PathOf("src/Shop.Web/Program.cs")) + "// unstaged\n");
        _repo.Write("scratch/untracked.cs", "// untracked\n");
        _repo.Write("debug.log", "ignored\n");

        _repo.Git("status", "--porcelain");
        var before = Fingerprint();
        await Repositories.OpenAsync(new LocalBranchTarget(_repo.Root, "main", LocalHeadKind.WorkingTree), CancellationToken.None);
        var review = await Repositories.OpenAsync(new LocalBranchTarget(_repo.Root, "main", LocalHeadKind.Branch, FixtureRepository.HeadBranch), CancellationToken.None);
        await review.Checkout.GetDiffAsync(CancellationToken.None);
        await review.Checkout.ReadHeadFileAsync("src/Shop.Web/Program.cs", CancellationToken.None);
        var after = Fingerprint();

        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Index, after.Index);
        Assert.Equal(before.Refs, after.Refs);
        Assert.Equal(before.Head, after.Head);
        Assert.Equal(before.Files, after.Files);
        Assert.Equal(before.Stash, after.Stash);
    }

    [Fact]
    public async Task UnknownRefsAndFoldersAreExplained()
    {
        var missingBase = await Assert.ThrowsAsync<ArgumentException>(() => Repositories.OpenAsync(new LocalBranchTarget(_repo.Root, "nope", LocalHeadKind.WorkingTree), CancellationToken.None));
        var missingFolder = await Assert.ThrowsAsync<ArgumentException>(() => Repositories.OpenAsync(new LocalBranchTarget(Path.Combine(_repo.Root, "missing"), "main", LocalHeadKind.WorkingTree), CancellationToken.None));
        var optionLike = await Assert.ThrowsAsync<ArgumentException>(() => Repositories.OpenAsync(new LocalBranchTarget(_repo.Root, "main", LocalHeadKind.Branch, "--output=x"), CancellationToken.None));

        Assert.Contains("'nope'", missingBase.Message, StringComparison.Ordinal);
        Assert.Contains("isn't a folder", missingFolder.Message, StringComparison.Ordinal);
        Assert.Contains("--output=x", optionLike.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDefaultBaseIsMainWhenThereIsNoRemote()
    {
        Assert.Equal("main", await Repositories.DefaultBaseAsync(_repo.Root, CancellationToken.None));
    }

    private (string Status, string Index, string Refs, string Head, string Files, string Stash) Fingerprint()
    {
        var index = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_repo.Root, ".git", "index"))))
            + File.GetLastWriteTimeUtc(Path.Combine(_repo.Root, ".git", "index")).Ticks;
        var files = string.Join('\n', Directory.EnumerateFiles(_repo.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(f => $"{Path.GetRelativePath(_repo.Root, f)} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))} {File.GetLastWriteTimeUtc(f).Ticks}"));
        return (
            // --no-optional-locks: git status otherwise refreshes the index itself, which would hide what the product did.
            _repo.Git("--no-optional-locks", "status", "--porcelain=v2", "--untracked-files=all", "--ignored"),
            index,
            _repo.Git("for-each-ref", "--format=%(refname) %(objectname)"),
            File.ReadAllText(Path.Combine(_repo.Root, ".git", "HEAD")),
            files,
            _repo.Git("stash", "list"));
    }
}
