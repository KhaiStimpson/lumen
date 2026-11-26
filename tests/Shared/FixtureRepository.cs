using System.Diagnostics;

namespace Lumen.TestSupport;

/// <summary>
/// Builds a throwaway git repository from the Razor Pages fixture (tests/fixtures/razor-pages-app): the base tree on
/// <c>main</c>, the head tree committed on <c>feature/ai-change</c>. Optionally leaves extra uncommitted edits in the
/// working tree for local-branch tests. Shells out to the git CLI; no network.
/// </summary>
public sealed class FixtureRepository : IDisposable
{
    private FixtureRepository(string root) => Root = root;

    public string Root { get; }

    public string BaseSha { get; private set; } = "";

    public string HeadSha { get; private set; } = "";

    public const string HeadBranch = "feature/ai-change";

    /// <summary>The fixture folder (base/, head/, answer-key.json), found from the test's output directory upwards.</summary>
    public static string FixtureDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "razor-pages-app")))
            {
                dir = dir.Parent;
            }

            return dir is null
                ? throw new DirectoryNotFoundException("tests/fixtures/razor-pages-app not found above " + AppContext.BaseDirectory)
                : Path.Combine(dir.FullName, "tests", "fixtures", "razor-pages-app");
        }
    }

    public static FixtureRepository Create(bool checkoutHead = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "lumen-fixture-repos", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        var repo = new FixtureRepository(root);
        repo.Git("init", "-q", "-b", "main");
        repo.Git("config", "core.autocrlf", "false");
        repo.Git("config", "user.name", "Fixture");
        repo.Git("config", "user.email", "fixture@example.invalid");
        CopyTree(Path.Combine(FixtureDirectory, "base"), root);
        repo.Git("add", "-A");
        repo.Git("commit", "-q", "-m", "Contoso Shop: base");
        repo.BaseSha = repo.Git("rev-parse", "HEAD").Trim();
        repo.Git("checkout", "-q", "-b", HeadBranch);
        ClearTree(root);
        CopyTree(Path.Combine(FixtureDirectory, "head"), root);
        repo.Git("add", "-A");
        repo.Git("commit", "-q", "-m", "AI change: invoices, notes, bulk edit, dashboard");
        repo.HeadSha = repo.Git("rev-parse", "HEAD").Trim();
        if (!checkoutHead)
        {
            repo.Git("checkout", "-q", "main");
        }

        return repo;
    }

    public string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var options = args.TakeWhile(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
        foreach (var option in options)
        {
            start.ArgumentList.Add(option);
        }

        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.autocrlf=false");
        foreach (var arg in args.Skip(options.Count))
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error.Result}");
        }

        return output.Result;
    }

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Write(string relative, string text)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void ClearTree(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root).Where(d => Path.GetFileName(d) != ".git"))
        {
            Directory.Delete(dir, recursive: true);
        }

        foreach (var file in Directory.EnumerateFiles(root))
        {
            File.Delete(file);
        }
    }
}
