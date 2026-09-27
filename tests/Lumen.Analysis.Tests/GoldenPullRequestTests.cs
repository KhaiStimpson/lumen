using Lumen.Analysis.Tests.Support;
using Lumen.Domain;
using Lumen.Roslyn;
using Xunit.Abstractions;

namespace Lumen.Analysis.Tests;

/// <summary>
/// Runs the full pipeline against a real local checkout. Opt-in: set LUMEN_GOLDEN_CHECKOUT to a clone whose
/// working tree is at the PR head, and LUMEN_GOLDEN_BASE to the base ref (e.g. origin/dev).
/// </summary>
public sealed class GoldenPullRequestTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ReportsReviewPointsForLocalCheckout()
    {
        var root = Environment.GetEnvironmentVariable("LUMEN_GOLDEN_CHECKOUT");
        var baseRef = Environment.GetEnvironmentVariable("LUMEN_GOLDEN_BASE");
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(baseRef))
        {
            return;
        }

        var head = await LocalGitCheckout.RevParse(root, "HEAD");
        var baseSha = await LocalGitCheckout.RevParse(root, baseRef);
        var checkout = new LocalGitCheckout(root, baseSha, head, await MergeBase(root, baseSha, head));

        var files = ChangedFiles.FromUnifiedDiff(await checkout.GetDiffAsync(CancellationToken.None));
        var snapshot = new PullRequestSnapshot(
            new PullRequestKey(new RepositoryRef("local", "golden"), 1),
            baseSha, head, checkout.MergeBaseSha,
            new PullRequestMetadata("golden", "me", "open", false, baseRef, "HEAD", "", null, DateTimeOffset.UtcNow),
            files, []);

        var pipeline = new ReviewPointPipeline([new PeerPatternDetector()], new RuleBasedAttentionPolicy(), [new PeerDeviationExplainer()]);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await pipeline.RunAsync(new AnalysisContext(snapshot, checkout), null, CancellationToken.None);

        output.WriteLine($"{files.Count} files, {files.Count(f => f.Mechanical.IsMechanical)} mechanical, analysed in {sw.ElapsedMilliseconds} ms");
        foreach (var p in result.ReviewPoints)
        {
            output.WriteLine($"\n[{p.Severity}] {p.Title}  ({p.EvidenceState}; {p.RoutingReason})");
            output.WriteLine($"  @ {p.Anchor.Path}:{p.Anchor.StartLine}  +{p.OtherLocations.Count} more");
            output.WriteLine($"  {p.Summary}");
            output.WriteLine($"  WHY: {p.WhyItMatters}");
            output.WriteLine($"  COMMENT: {p.SuggestedComment}");
        }

        output.WriteLine($"\n--- suppressed ({result.Suppressed.Count})");
        foreach (var (c, d) in result.Suppressed.Take(40))
        {
            var f = (PeerDeviationFacts)c.Facts;
            output.WriteLine($"  {f.Role.Key} / {f.Missing.Key} [{string.Join(",", f.Affected.Select(a => a.Type.Name))}] — {d.Reason}");
        }

        output.WriteLine($"\n--- conventions ({result.Conventions.Count})");
        foreach (var c in result.Conventions.Take(30))
        {
            output.WriteLine($"  {c.Statement} ({c.Supporting}/{c.PeerCount})");
        }
    }

    private static async Task<string> MergeBase(string root, string a, string b)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", ["merge-base", a, b]) { WorkingDirectory = root, RedirectStandardOutput = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var s = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return s.Trim();
    }
}
