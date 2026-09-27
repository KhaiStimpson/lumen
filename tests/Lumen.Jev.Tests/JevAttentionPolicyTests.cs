using Lumen.Analysis;
using Lumen.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Q = Lumen.Jev.AttentionQuestions;
using static Lumen.Jev.Tests.Support;

namespace Lumen.Jev.Tests;

public sealed class JevAttentionPolicyTests
{
    private sealed class FakeEvaluator(Func<SystemOneState, IReadOnlyList<SystemOneQuestion>, Task<SystemOneResult>> answer) : ISystemOneEvaluator
    {
        public bool IsConfigured { get; init; } = true;

        public string Model => "typesafe/jev-1.13";

        public List<SystemOneState> States { get; } = [];

        public Task<SystemOneResult> EvaluateAsync(SystemOneState state, IReadOnlyList<SystemOneQuestion> questions, CancellationToken cancellationToken)
        {
            lock (States)
            {
                States.Add(state);
            }

            return answer(state, questions);
        }

        /// <summary>Answers every question with the probability given for its question id (default 0.5).</summary>
        public static FakeEvaluator Answering(IReadOnlyDictionary<string, double> byQuestion) => new((_, questions) =>
            Task.FromResult(new SystemOneResult(
                questions.ToDictionary(
                    q => q.Id,
                    q => Noul(q.Id, byQuestion.GetValueOrDefault(q.Id[(q.Id.IndexOf('_', StringComparison.Ordinal) + 1)..], 0.5))),
                "typesafe/jev-1.13",
                "typesafe/jev-1.13-20260917",
                "TypeSafe",
                0.00002,
                TimeSpan.FromMilliseconds(80))));
    }

    private static readonly RuleBasedAttentionPolicy Rules = new();

    private static JevAttentionPolicy Policy(
        ISystemOneEvaluator evaluator,
        MemoryEvaluationStore? store = null,
        PrivacySettings? privacy = null,
        JevPolicyOptions? options = null,
        TimeProvider? time = null) =>
        new(Rules, evaluator, store ?? new MemoryEvaluationStore(), () => privacy ?? new PrivacySettings(), options ?? new JevPolicyOptions(), time ?? TimeProvider.System, NullLogger<JevAttentionPolicy>.Instance);

    private static async Task<IReadOnlyList<AttentionDecision>> RulesFor(params Candidate[] candidates)
    {
        var decisions = new List<AttentionDecision>();
        foreach (var c in candidates)
        {
            decisions.Add(await Rules.DecideAsync(c, CancellationToken.None));
        }

        return decisions;
    }

    [Fact]
    public async Task FallsBackToRulesWithoutAKey()
    {
        var unconfigured = new FakeEvaluator((_, _) => throw new InvalidOperationException("must not be called")) { IsConfigured = false };
        var candidates = new[] { Candidate("a"), Candidate("b", peers: 2) };
        var policy = Policy(unconfigured);

        var decisions = await ((IAttentionPolicy)policy).DecideAllAsync(candidates, Context(), CancellationToken.None);

        Assert.Equal(await RulesFor(candidates), decisions);
        Assert.Contains("no OpenRouter key", policy.LastStatus, StringComparison.Ordinal);
        Assert.Empty(unconfigured.States);
    }

    [Fact]
    public async Task RespectsPrivacySettings()
    {
        var evaluator = FakeEvaluator.Answering(new Dictionary<string, double>());
        var policy = Policy(evaluator, privacy: new PrivacySettings { AllowCloudReasoning = false });

        var decisions = await ((IAttentionPolicy)policy).DecideAllAsync([Candidate("a")], Context(), CancellationToken.None);

        Assert.Equal(await RulesFor(Candidate("a")), decisions);
        Assert.Empty(evaluator.States);
    }

    [Theory]
    [InlineData(SystemOneFailure.Unavailable)]
    [InlineData(SystemOneFailure.InvalidResponse)]
    [InlineData(SystemOneFailure.Unauthorized)]
    public async Task FallsBackToRulesWhenJevFails(SystemOneFailure failure)
    {
        var failing = new FakeEvaluator((_, _) => throw new SystemOneUnavailableException(failure, "boom"));
        var policy = Policy(failing);

        var decisions = await ((IAttentionPolicy)policy).DecideAllAsync([Candidate("a")], Context(), CancellationToken.None);

        Assert.Equal(await RulesFor(Candidate("a")), decisions);
        Assert.Contains("boom", policy.LastStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FallsBackToRulesOnUnexpectedExceptions()
    {
        var broken = new FakeEvaluator((_, _) => throw new InvalidOperationException("bug"));

        var decisions = await ((IAttentionPolicy)Policy(broken)).DecideAllAsync([Candidate("a")], Context(), CancellationToken.None);

        Assert.Equal(await RulesFor(Candidate("a")), decisions);
    }

    [Fact]
    public async Task NeverWaitsLongerThanItsBudget()
    {
        var hanging = new FakeEvaluator(async (_, _) =>
        {
            await Task.Delay(Timeout.Infinite);
            throw new InvalidOperationException();
        });
        var policy = Policy(hanging, options: new JevPolicyOptions { OverallTimeout = TimeSpan.FromMilliseconds(100) });

        var started = DateTime.UtcNow;
        var decisions = await ((IAttentionPolicy)policy).DecideAllAsync([Candidate("a")], Context(), CancellationToken.None);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal(await RulesFor(Candidate("a")), decisions);
    }

    [Fact]
    public async Task PausesAfterAPersistentFailure()
    {
        var calls = 0;
        var unauthorized = new FakeEvaluator((_, _) =>
        {
            calls++;
            throw new SystemOneUnavailableException(SystemOneFailure.Unauthorized, "bad key");
        });
        var policy = Policy(unauthorized);

        await ((IAttentionPolicy)policy).DecideAllAsync([Candidate("a")], Context(), CancellationToken.None);
        await ((IAttentionPolicy)policy).DecideAllAsync([Candidate("a")], Context(), CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Contains("paused", policy.LastStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchesOneRequestPerChangeUnitAndOnlyAsksAboutSurfacedCandidates()
    {
        var evaluator = FakeEvaluator.Answering(new Dictionary<string, double>());
        var candidates = new[]
        {
            Candidate("a", "cu-1"),
            Candidate("b", "cu-1"),
            Candidate("c", "cu-2"),
            Candidate("thin", "cu-3", peers: 2, supporting: 2),
        };

        await ((IAttentionPolicy)Policy(evaluator)).DecideAllAsync(candidates, Context(), CancellationToken.None);

        Assert.Equal(2, evaluator.States.Count);
        Assert.Equal([1, 2], evaluator.States.Select(s => s.Json["candidates"]!.AsArray().Count).Order());
    }

    [Fact]
    public async Task PersistsEveryAnswerWithModelAndSchemaVersion()
    {
        var store = new MemoryEvaluationStore();

        await ((IAttentionPolicy)Policy(FakeEvaluator.Answering(new Dictionary<string, double> { [Q.HumanJudgement] = 0.9 }), store))
            .DecideAllAsync([Candidate("a")], Context(), CancellationToken.None);

        Assert.Equal(Q.Ids.Count, store.Records.Count);
        var judgement = Assert.Single(store.Records, r => r.QuestionId == Q.HumanJudgement);
        Assert.Equal("a", judgement.CandidateKey);
        Assert.Equal("cu-1", judgement.ChangeUnitId);
        Assert.Equal("head", judgement.HeadSha);
        Assert.Equal(Q.SchemaVersion, judgement.QuestionSchemaVersion);
        Assert.Equal("typesafe/jev-1.13", judgement.Model);
        Assert.Equal("typesafe/jev-1.13-20260917", judgement.ResolvedModel);
        Assert.Equal(0.9, judgement.Probabilities!["true"], 3);
        Assert.Contains("\"peers\":8", judgement.StateJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuppressesMechanicalChanges()
    {
        var decisions = await ((IAttentionPolicy)Policy(FakeEvaluator.Answering(new Dictionary<string, double> { [Q.Mechanical] = 0.97 })))
            .DecideAllAsync([Candidate("a")], Context(), CancellationToken.None);

        Assert.Equal(AttentionAction.Suppress, decisions[0].Action);
        Assert.Contains("mechanical", decisions[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeverSurfacesWhatTheRulesSuppressed()
    {
        var thin = Candidate("thin", peers: 2, supporting: 2);
        var eager = FakeEvaluator.Answering(new Dictionary<string, double> { [Q.HumanJudgement] = 1, [Q.PrecedentDeviation] = 1 });

        var decisions = await ((IAttentionPolicy)Policy(eager)).DecideAllAsync([thin], Context(), CancellationToken.None);

        Assert.Equal(AttentionAction.Suppress, decisions[0].Action);
        Assert.Empty(eager.States);
    }

    [Fact]
    public void RoutesToInvestigationsAndRaisesPriority()
    {
        var rule = new AttentionDecision(AttentionAction.Surface, ReviewSeverity.Medium, 20, "8/8 peers");
        var answers = new Dictionary<string, SystemOneAnswer>
        {
            [Q.PrecedentDeviation] = Noul(Q.PrecedentDeviation, 0.9),
            [Q.HistoryUseful] = Noul(Q.HistoryUseful, 0.7),
            [Q.CorrectnessInvestigation] = Noul(Q.CorrectnessInvestigation, 0.75),
            [Q.ArchitectureInvestigation] = Noul(Q.ArchitectureInvestigation, 0.2),
            [Q.HumanJudgement] = Noul(Q.HumanJudgement, 0.85),
            [Q.ObservableBehaviour] = Noul(Q.ObservableBehaviour, 0.5),
        };

        var decision = JevAttentionPolicy.Combine(rule, answers, new JevThresholds(), "typesafe/jev-1.13");

        Assert.Equal(AttentionAction.Investigate, decision.Action);
        Assert.Equal(ReviewSeverity.Medium, decision.Severity);
        Assert.Equal(
            [new InvestigationRequest(InvestigationType.RepositoryPattern, InvestigationBudget.Standard), new InvestigationRequest(InvestigationType.Correctness, InvestigationBudget.Standard)],
            decision.Investigations);
        Assert.Equal(20 + (4 * 0.85) + (2 * 0.5), decision.Priority, 6);
        Assert.StartsWith("8/8 peers · JEV typesafe/jev-1.13: judgement 0.85", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SuppressesOnlyWhenJudgementAndDeviationAreBothNegligible()
    {
        var rule = new AttentionDecision(AttentionAction.Surface, ReviewSeverity.Low, 10, "rule");
        Dictionary<string, SystemOneAnswer> With(double judgement, double deviation) => new()
        {
            [Q.HumanJudgement] = Noul(Q.HumanJudgement, judgement),
            [Q.PrecedentDeviation] = Noul(Q.PrecedentDeviation, deviation),
        };

        Assert.Equal(AttentionAction.Suppress, JevAttentionPolicy.Combine(rule, With(0.01, 0.02), new JevThresholds(), "m").Action);
        Assert.Equal(AttentionAction.Surface, JevAttentionPolicy.Combine(rule, With(0.01, 0.5), new JevThresholds(), "m").Action);
        Assert.Equal(AttentionAction.Surface, JevAttentionPolicy.Combine(rule, With(0.3, 0.02), new JevThresholds(), "m").Action);
        Assert.Equal(rule, JevAttentionPolicy.Combine(rule, new Dictionary<string, SystemOneAnswer>(), new JevThresholds(), "m"));
    }
}
