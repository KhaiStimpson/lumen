using System.Diagnostics;
using Lumen.Agents.Execution;

namespace Lumen.Agents.Tests;

public sealed class BoundedOutputTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundsOutputBeforeBufferingAndPreservesUtf8Lines(bool fitsLimit)
    {
        var root = Path.Combine(Path.GetTempPath(), $"lumen-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await GitAsync(root, "init", "-q");
            var path = Path.Combine(root, "output.txt");
            var text = fitsLimit ? new string('x', 4095) + "😀\r\ncafé\rend\ntail" : new string('x', 128 * 1024);
            await File.WriteAllTextAsync(path, text);
            var hash = (await GitAsync(root, "hash-object", "-w", path)).Trim();
            var runner = new SandboxedProcessRunner(new CommandPolicy
            {
                AllowedExecutables = new HashSet<string>(StringComparer.Ordinal) { "git" },
                AllowedWorkingRoots = [root],
            });
            var lines = new List<string>();
            var result = await runner.RunAsync(new SandboxedCommand
            {
                Executable = "git", Arguments = ["cat-file", "blob", hash], WorkingDirectory = root,
                Timeout = TimeSpan.FromSeconds(10), MaxOutputBytes = fitsLimit ? System.Text.Encoding.UTF8.GetByteCount(text) : 64,
            }, line => { lines.Add(line); return ValueTask.CompletedTask; }, CancellationToken.None);

            if (fitsLimit)
            {
                Assert.True(result.Succeeded, result.StandardErrorTail);
                Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(text), result.OutputBytes);
                Assert.Equal([new string('x', 4095) + "😀", "café", "end", "tail"], lines);
            }
            else
            {
                Assert.Equal(ProcessEnd.OutputLimitExceeded, result.End);
                Assert.Equal(65, result.OutputBytes);
                Assert.Empty(lines);
            }
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> GitAsync(string root, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) { info.ArgumentList.Add(argument); }
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await errors);
        return await output;
    }
}
