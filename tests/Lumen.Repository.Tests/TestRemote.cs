using System.Text;
using Lumen.Domain;

namespace Lumen.Repository.Tests;

/// <summary>
/// A throwaway local "GitHub" repository: main has base commits, a feature branch holds the PR,
/// and only refs/pull/7/head (as for a fork PR) points at the feature tip, so a clone does not see it.
/// </summary>
internal sealed class TestRemote : IDisposable
{
    public const int PullNumber = 7;
    public const int BigFileLines = 20_000;

    private static readonly GitRunner Git = new();

    private TestRemote(string root)
    {
        Root = root;
        RepoPath = Path.Combine(root, "remote");
        CacheRoot = Path.Combine(root, "cache");
    }

    public string Root { get; }

    public string RepoPath { get; }

    public string CacheRoot { get; }

    public string Url => new Uri(RepoPath).AbsoluteUri;

    public string ForkPointSha { get; private set; } = string.Empty;

    public string BaseSha { get; private set; } = string.Empty;

    public string HeadSha { get; private set; } = string.Empty;

    public static PullRequestKey Key { get; } = new(new RepositoryRef("octo", "widgets"), PullNumber);

    public static string BigFileBase { get; } = BuildBigFile(changed: false);

    public static string BigFileHead { get; } = BuildBigFile(changed: true);

    public static async Task<TestRemote> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "lumen-repo-tests", Guid.NewGuid().ToString("N")[..12]);
        var remote = new TestRemote(root);
        Directory.CreateDirectory(remote.RepoPath);
        await remote.InitializeAsync();
        return remote;
    }

    public RepositoryWorkspaceOptions Options(Func<CancellationToken, Task<string?>>? tokenProvider = null) => new()
    {
        CacheRoot = CacheRoot,
        RemoteUrl = _ => Url,
        TokenProvider = tokenProvider,
    };

    public async Task<string> AddFeatureCommitAsync(string file, string content)
    {
        await RunAsync("checkout", "-q", "--detach", $"refs/pull/{PullNumber}/head");
        await WriteAsync(file, content);
        await RunAsync("add", "-A");
        await RunAsync("commit", "-q", "-m", "more feature");
        HeadSha = await RevParseAsync("HEAD");
        await RunAsync("update-ref", $"refs/pull/{PullNumber}/head", HeadSha);
        await RunAsync("checkout", "-q", "main");
        return HeadSha;
    }

    public Task<GitResult> RunAsync(params string[] args) =>
        Git.RunAsync(RepoPath, ["-c", "user.name=t", "-c", "user.email=t@t", "-c", "core.autocrlf=false", .. args], CancellationToken.None);

    public void Dispose() => GitRepositoryWorkspace.DeleteDirectory(Root);

    private async Task InitializeAsync()
    {
        await RunAsync("init", "-q", "-b", "main");
        await RunAsync("config", "uploadpack.allowFilter", "true");

        await WriteAsync("README.md", "# Widgets\n");
        await WriteAsync("src/Widget.cs", "class Widget\n{\n    int Size => 1;\n}\n");
        await WriteAsync("src/Old.cs", "class Old { }\n");
        await WriteAsync("data/big.txt", BigFileBase);
        await RunAsync("add", "-A");
        await RunAsync("commit", "-q", "-m", "base");
        ForkPointSha = await RevParseAsync("HEAD");

        await RunAsync("checkout", "-q", "-b", "feature");
        await WriteAsync("src/Widget.cs", "class Widget\n{\n    int Size => 2;\n}\n");
        await WriteAsync("src/Added.cs", "class Added { }\n");
        await WriteAsync("data/big.txt", BigFileHead);
        await RunAsync("rm", "-q", "src/Old.cs");
        await RunAsync("add", "-A");
        await RunAsync("commit", "-q", "-m", "feature");
        HeadSha = await RevParseAsync("HEAD");
        await RunAsync("update-ref", $"refs/pull/{PullNumber}/head", HeadSha);

        await RunAsync("checkout", "-q", "main");
        await RunAsync("branch", "-q", "-D", "feature");
        await WriteAsync("README.md", "# Widgets\n\nMain moved on.\n");
        await RunAsync("commit", "-q", "-am", "main moves");
        BaseSha = await RevParseAsync("HEAD");
    }

    private async Task WriteAsync(string relative, string content)
    {
        var path = Path.Combine(RepoPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private async Task<string> RevParseAsync(string rev) =>
        (await Git.RunAsync(RepoPath, ["rev-parse", rev], CancellationToken.None)).StandardOutput.Trim();

    private static string BuildBigFile(bool changed)
    {
        var builder = new StringBuilder();
        for (var i = 1; i <= BigFileLines; i++)
        {
            var modified = changed && i % 1000 == 0;
            builder.Append(modified ? $"line {i} changed\n" : $"line {i}\n");
        }

        return builder.ToString();
    }
}
