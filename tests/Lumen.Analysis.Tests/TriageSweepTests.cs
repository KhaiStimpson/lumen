using System.Diagnostics;
using System.Text;
using Lumen.Analysis.Tests.Support;
using Lumen.Domain;
using Lumen.Roslyn.Flags;
using Lumen.Roslyn.Triage;
using Xunit.Abstractions;

namespace Lumen.Analysis.Tests;

/// <summary>
/// Safety sweep for the "mechanical must be proven" invariant: triages each of the last N commits of a local clone
/// and prints every hunk proven mechanical, and every boilerplate fold, with its diff, and every red flag with its line, for a human (or reviewer) to check for false positives.
/// Opt-in: LUMEN_TRIAGE_SWEEP=&lt;path to a local clone&gt;, optional LUMEN_TRIAGE_SWEEP_COUNT (default 40). Local git only.
/// </summary>
public sealed class TriageSweepTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PrintsEveryProvenMechanicalHunkInRecentCommits()
    {
        var repo = Environment.GetEnvironmentVariable("LUMEN_TRIAGE_SWEEP");
        if (string.IsNullOrEmpty(repo))
        {
            return;
        }

        var count = int.TryParse(Environment.GetEnvironmentVariable("LUMEN_TRIAGE_SWEEP_COUNT"), out var n) ? n : 40;
        var commits = (await GitAsync(repo, "log", "--no-merges", "--format=%H", $"-{count}", "HEAD"))!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var totals = new Dictionary<ChangeClass, int>();
        var folds = new Dictionary<FoldKind, int>();
        var flagCounts = new Dictionary<FlagKind, int>();
        foreach (var sha in commits)
        {
            var diff = await GitAsync(repo, "diff", "--no-color", "-U3", $"{sha}~1", sha);
            if (diff is null)
            {
                continue;
            }

            var files = ChangedFiles.FromUnifiedDiff(diff);
            var baseTexts = new Dictionary<string, string>(StringComparer.Ordinal);
            var headTexts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in files.Where(f => !f.IsBinary))
            {
                if (f.Kind != FileChangeKind.Added && await GitAsync(repo, "show", $"{sha}~1:{f.OldPath ?? f.Path}") is { } b)
                {
                    baseTexts[f.Path] = b;
                }

                if (f.Kind != FileChangeKind.Deleted && await GitAsync(repo, "show", $"{sha}:{f.Path}") is { } h)
                {
                    headTexts[f.Path] = h;
                }
            }

            var snapshot = TestDiffs.Snapshot(files);
            var sources = new TriageSources(baseTexts, headTexts);
            var flags = RedFlags.Detect(snapshot, sources, new ReviewSettings());
            var triage = RedFlags.OpenFlaggedFolds(RoslynTriage.CreatePipeline().Run(snapshot, sources), flags);
            foreach (var flag in flags)
            {
                flagCounts[flag.Kind] = flagCounts.GetValueOrDefault(flag.Kind) + 1;
                var text = (flag.Side == DiffSide.Right ? headTexts : baseTexts).GetValueOrDefault(flag.Path)?.Split('\n') is { } lines && flag.Line <= lines.Length ? lines[flag.Line - 1].Trim() : "";
                output.WriteLine($"FLAG {sha[..8]} {flag.Kind} {flag.Path}:{(flag.Side == DiffSide.Right ? "head" : "base")}:{flag.Line} — {flag.Message} | {text}");
            }

            foreach (var hunk in triage.Hunks)
            {
                totals[hunk.Class] = totals.GetValueOrDefault(hunk.Class) + hunk.ChangedLines;
                folds[hunk.Fold] = folds.GetValueOrDefault(hunk.Fold) + hunk.ChangedLines;
            }

            foreach (var hunk in triage.Hunks.Where(h => (h.Fold == FoldKind.Mechanical && h.Class != ChangeClass.Generated) || h.Fold == FoldKind.Boilerplate))
            {
                var sb = new StringBuilder();
                sb.Append("=== ").Append(sha[..8]).Append(' ').Append(hunk.Fold).Append('/').Append(hunk.Class).Append(' ').Append(hunk.Path)
                  .Append(" old ").Append(hunk.OldStart).Append('-').Append(hunk.OldEnd).Append(" new ").Append(hunk.NewStart).Append('-').Append(hunk.NewEnd)
                  .Append(" — ").AppendLine(string.Join("; ", hunk.Reasons));
                var file = files.First(f => f.Path == hunk.Path);
                foreach (var line in file.Hunks.SelectMany(h => h.Lines).Where(l =>
                             (l.Kind == DiffLineKind.Removed && l.OldNumber >= hunk.OldStart && l.OldNumber <= hunk.OldEnd) ||
                             (l.Kind == DiffLineKind.Added && l.NewNumber >= hunk.NewStart && l.NewNumber <= hunk.NewEnd)).Take(30))
                {
                    sb.Append(line.Kind == DiffLineKind.Added ? '+' : '-').AppendLine(line.Text);
                }

                output.WriteLine(sb.ToString());
            }
        }

        output.WriteLine($"{commits.Length} commits. Changed lines per class: " + string.Join(", ", totals.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))
            + ". Per fold: " + string.Join(", ", folds.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))
            + ". Flags: " + string.Join(", ", flagCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
    }

    private static async Task<string?> GitAsync(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("git", ["-C", repo, .. args]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(psi)!;
        var text = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode == 0 ? text : null;
    }
}
