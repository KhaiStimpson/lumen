using Lumen.Analysis;
using Lumen.Domain;
using Lumen.Storage;
using Xunit.Abstractions;
using static Lumen.Jev.Tests.Support;

namespace Lumen.Jev.Tests;

/// <summary>
/// Calls the real OpenRouter Decisions API with a synthetic, source-free state. Opt-in: set LUMEN_LIVE_JEV=1 after
/// storing a key with <c>Lumen.Engine.exe connections set-openrouter-key</c>. Costs a fraction of a cent.
/// </summary>
public sealed class LiveJevTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AnswersEveryAttentionQuestionForARealCandidate()
    {
        if (Environment.GetEnvironmentVariable("LUMEN_LIVE_JEV") != "1" || !OperatingSystem.IsWindows())
        {
            return;
        }

        using var http = new HttpClient();
        var evaluator = new OpenRouterSystemOneEvaluator(http, new WindowsCredentialStore(), new OpenRouterOptions { RequestTimeout = TimeSpan.FromSeconds(30) });
        Assert.True(evaluator.IsConfigured, "No OpenRouter key in Credential Manager.");

        var key = await evaluator.CheckKeyAsync(CancellationToken.None);
        output.WriteLine(key.Detail);
        Assert.True(key.Valid, key.Detail);

        var candidate = Candidate("live");
        var rule = await new RuleBasedAttentionPolicy().DecideAsync(candidate, CancellationToken.None);
        var state = SystemOneStateBuilder.Build(Context(), [("c0", candidate, rule)], new PrivacySettings());
        output.WriteLine(state.ToJsonString());

        var result = await evaluator.EvaluateAsync(state, AttentionQuestions.For(["c0"]), CancellationToken.None);

        output.WriteLine($"{result.ResolvedModel} via {result.Provider}, {result.Latency.TotalMilliseconds:0} ms, cost {result.Cost}");
        foreach (var (id, answer) in result.Answers)
        {
            output.WriteLine($"{id}: {answer.PTrue:0.00}");
        }

        Assert.Equal(AttentionQuestions.Ids.Count, result.Answers.Count);
        Assert.All(result.Answers.Values, a => Assert.InRange(a.PTrue, 0, 1));
    }
}
