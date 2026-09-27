using System.Diagnostics;
using System.Globalization;
using Lumen.Analysis;
using Lumen.Domain;
using Microsoft.Extensions.Logging;
using Q = Lumen.Jev.AttentionQuestions;

namespace Lumen.Jev;

/// <summary>Routing thresholds in the spirit of TDD §10.3. Meant to become adaptive once answers are calibrated.</summary>
public sealed record JevThresholds
{
    public double SuppressMechanical { get; init; } = 0.95;

    /// <summary>Suppress only when JEV is confident no judgement is needed <em>and</em> sees no deviation.</summary>
    public double SuppressBelowJudgement { get; init; } = 0.05;

    public double SuppressBelowDeviation { get; init; } = 0.10;

    public double PrecedentInvestigation { get; init; } = 0.75;

    public double CorrectnessInvestigation { get; init; } = 0.70;

    public double ArchitectureInvestigation { get; init; } = 0.80;
}

public sealed record JevPolicyOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>JEV gets this long per analysis; anything unanswered keeps its rule-based decision (§38.2).</summary>
    public TimeSpan OverallTimeout { get; init; } = TimeSpan.FromSeconds(12);

    public int MaxCandidatesPerRequest { get; init; } = 5;

    public int MaxParallelRequests { get; init; } = 4;

    /// <summary>After a bad key or exhausted credits, stop calling for this long instead of failing every review.</summary>
    public TimeSpan PauseAfterPersistentFailure { get; init; } = TimeSpan.FromMinutes(15);

    public JevThresholds Thresholds { get; init; } = new();
}

/// <summary>
/// Rule-based attention, refined by JEV (TDD §10, §38). The rules always run first and remain both the floor and the
/// fallback: JEV may suppress a mechanical change, re-rank, and route candidates to investigation, but it never
/// surfaces what the deterministic rules rejected (§50: JEV must not be the sole signal). Any JEV failure — no key,
/// privacy settings, timeout, bad response — leaves the rule decision in place, so JEV never blocks a review.
/// </summary>
public sealed partial class JevAttentionPolicy(
    RuleBasedAttentionPolicy rules,
    ISystemOneEvaluator evaluator,
    IAttentionEvaluationStore store,
    Func<PrivacySettings> privacy,
    Func<JevPolicyOptions> currentOptions,
    TimeProvider time,
    ILogger<JevAttentionPolicy> logger) : IAttentionPolicy
{
    private readonly Lock _gate = new();
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    /// <summary>Read at each use, so a settings change applies to the next analysis without a restart.</summary>
    private JevPolicyOptions Options => currentOptions();

    /// <summary>Human-readable state of the last attempt, for connection diagnostics.</summary>
    public string LastStatus { get; private set; } = "Not used yet";

    public ValueTask<AttentionDecision> DecideAsync(Candidate candidate, CancellationToken cancellationToken) =>
        rules.DecideAsync(candidate, cancellationToken);

    public async ValueTask<IReadOnlyList<AttentionDecision>> DecideAllAsync(
        IReadOnlyList<Candidate> candidates,
        AnalysisContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(context);

        var decisions = new AttentionDecision[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            decisions[i] = await RuleBasedAttentionPolicy.DecideAsync(candidates[i], context.Settings.Sensitivity).ConfigureAwait(false);
        }

        var surfaced = Enumerable.Range(0, candidates.Count).Where(i => decisions[i].Action != AttentionAction.Suppress).ToList();
        if (surfaced.Count == 0)
        {
            return decisions;
        }

        if (!ShouldCallJev(out var skipReason))
        {
            LastStatus = skipReason;
            return decisions;
        }

        // One request per change unit (TDD §38.1), split only if a unit has many candidates.
        var batches = surfaced
            .GroupBy(i => candidates[i].ChangeUnits.Count > 0 ? candidates[i].ChangeUnits[0].Id : candidates[i].Key, StringComparer.Ordinal)
            .SelectMany(g => g.Chunk(Math.Max(1, Options.MaxCandidatesPerRequest)))
            .ToList();

        // Disposed only once every batch has finished, so a late batch never touches a disposed token or semaphore.
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Options.OverallTimeout);
        var throttle = new SemaphoreSlim(Math.Max(1, Options.MaxParallelRequests));
        var privacySettings = privacy();
        var ruleDecisions = decisions.ToArray();

        var tasks = batches.Select(async batch =>
        {
            await throttle.WaitAsync(budget.Token).ConfigureAwait(false);
            try
            {
                return (Batch: batch, Decisions: await EvaluateBatchAsync(batch, candidates, ruleDecisions, context, privacySettings, budget.Token).ConfigureAwait(false));
            }
            finally
            {
                throttle.Release();
            }
        }).ToList();

        var all = Task.WhenAll(tasks);
        _ = all.ContinueWith(
            _ =>
            {
                budget.Dispose();
                throttle.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // Never wait on an evaluator past the budget, even one that ignores cancellation (§38.2).
        await Task.WhenAny(all, Task.Delay(Options.OverallTimeout + TimeSpan.FromMilliseconds(250), CancellationToken.None)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var answered = 0;
        foreach (var task in tasks.Where(t => t.IsCompletedSuccessfully && t.Result.Decisions is not null))
        {
            var (batch, combined) = task.Result;
            for (var n = 0; n < batch.Length; n++)
            {
                decisions[batch[n]] = combined![n];
            }

            answered += batch.Length;
        }

        if (answered < surfaced.Count && !all.IsCompleted)
        {
            LastStatus = "JEV timed out; rule-based routing used";
        }

        if (answered > 0)
        {
            LastStatus = string.Create(CultureInfo.InvariantCulture, $"JEV ({evaluator.Model}) answered for {answered} of {surfaced.Count} candidates");
        }

        return decisions;
    }

    private bool ShouldCallJev(out string reason)
    {
        reason = "";
        if (!Options.Enabled)
        {
            reason = "JEV disabled in settings";
        }
        else if (!privacy().AllowsJev)
        {
            reason = "JEV off: cloud reasoning not allowed";
        }
        else if (!evaluator.IsConfigured)
        {
            reason = "JEV off: no OpenRouter key";
        }
        else if (time.GetUtcNow() < _pausedUntil)
        {
            reason = $"JEV paused after an error ({LastStatus})";
        }

        return reason.Length == 0;
    }

    /// <summary>The combined decisions for one batch, or null (keep the rule decisions) on any failure.</summary>
    private async Task<AttentionDecision[]?> EvaluateBatchAsync(
        int[] batch,
        IReadOnlyList<Candidate> candidates,
        AttentionDecision[] ruleDecisions,
        AnalysisContext context,
        PrivacySettings privacySettings,
        CancellationToken cancellationToken)
    {
        var entries = batch.Select((index, n) => ($"c{n}", candidates[index], ruleDecisions[index])).ToList();
        var state = SystemOneStateBuilder.Build(context, entries, privacySettings);
        var questions = Q.For(entries.Select(e => e.Item1));

        SystemOneResult result;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            result = await evaluator.EvaluateAsync(state, questions, cancellationToken).ConfigureAwait(false);
        }
        catch (SystemOneUnavailableException ex)
        {
            RecordFailure(ex);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogJevTimedOut(logger, stopwatch.ElapsedMilliseconds);
            LastStatus = "JEV timed out; rule-based routing used";
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordFailure(new SystemOneUnavailableException(SystemOneFailure.InvalidResponse, ex.Message, ex));
            return null;
        }

        var combined = new AttentionDecision[batch.Length];
        var records = new List<AttentionEvaluationRecord>();
        var stateJson = state.ToJsonString();
        for (var n = 0; n < entries.Count; n++)
        {
            var (id, candidate, _) = entries[n];
            var answers = Q.Ids
                .Select(q => result.Answers.GetValueOrDefault(Q.Key(id, q)))
                .Where(a => a is not null)
                .ToDictionary(a => a!.QuestionId[(id.Length + 1)..], a => a!, StringComparer.Ordinal);

            combined[n] = Combine(ruleDecisions[batch[n]], answers, Options.Thresholds, result.ResolvedModel ?? result.Model);
            records.AddRange(answers.Select(a => new AttentionEvaluationRecord
            {
                Key = context.Snapshot.Key,
                HeadSha = context.Snapshot.HeadSha,
                ChangeUnitId = candidate.ChangeUnits.Count > 0 ? candidate.ChangeUnits[0].Id : "",
                CandidateKey = candidate.Key,
                QuestionId = a.Key,
                QuestionSchemaVersion = Q.SchemaVersion,
                Choice = a.Value.Choice,
                Probabilities = a.Value.Probabilities,
                Model = result.Model,
                ResolvedModel = result.ResolvedModel,
                Provider = result.Provider,
                StateJson = stateJson,
                LatencyMs = (long)result.Latency.TotalMilliseconds,
                At = time.GetUtcNow(),
            }));
        }

        try
        {
            await store.RecordEvaluationsAsync(records, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPersistFailed(logger, ex);
        }

        return combined;
    }

    /// <summary>Combines a rule decision with JEV's answers for the same candidate (TDD §10.3).</summary>
    public static AttentionDecision Combine(
        AttentionDecision rule,
        IReadOnlyDictionary<string, SystemOneAnswer> answers,
        JevThresholds thresholds,
        string model)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(thresholds);

        if (rule.Action == AttentionAction.Suppress || answers.Count == 0)
        {
            return rule;
        }

        double P(string question) => answers.TryGetValue(question, out var a) ? a.PTrue : double.NaN;
        bool Above(string question, double threshold) => P(question) is var p && !double.IsNaN(p) && p > threshold;
        bool Below(string question, double threshold) => P(question) is var p && !double.IsNaN(p) && p < threshold;

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"JEV {model}: judgement {Show(P(Q.HumanJudgement))}, deviation {Show(P(Q.PrecedentDeviation))}, mechanical {Show(P(Q.Mechanical))}");

        if (Above(Q.Mechanical, thresholds.SuppressMechanical))
        {
            return rule with { Action = AttentionAction.Suppress, Priority = 0, Reason = $"{rule.Reason} · {summary} → mechanical", Source = "jev" };
        }

        if (Below(Q.HumanJudgement, thresholds.SuppressBelowJudgement) && Below(Q.PrecedentDeviation, thresholds.SuppressBelowDeviation))
        {
            return rule with { Action = AttentionAction.Suppress, Priority = 0, Reason = $"{rule.Reason} · {summary} → no judgement needed", Source = "jev" };
        }

        var investigations = new List<InvestigationRequest>();
        if (Above(Q.PrecedentDeviation, thresholds.PrecedentInvestigation) &&
            (Above(Q.HistoryUseful, 0.5) || Below(Q.EvidenceSufficient, 0.5)))
        {
            investigations.Add(new InvestigationRequest(InvestigationType.RepositoryPattern, InvestigationBudget.Standard));
        }

        if (Above(Q.CorrectnessInvestigation, thresholds.CorrectnessInvestigation))
        {
            investigations.Add(new InvestigationRequest(InvestigationType.Correctness, InvestigationBudget.Standard));
        }

        if (Above(Q.ArchitectureInvestigation, thresholds.ArchitectureInvestigation))
        {
            investigations.Add(new InvestigationRequest(InvestigationType.Architecture, InvestigationBudget.Standard));
        }

        var boost = (4 * Zero(P(Q.HumanJudgement))) + (2 * Zero(P(Q.ObservableBehaviour)));
        return rule with
        {
            Action = investigations.Count > 0 ? AttentionAction.Investigate : rule.Action,
            Priority = rule.Priority + boost,
            Reason = $"{rule.Reason} · {summary}",
            Investigations = investigations,
            Source = "jev",
        };
    }

    private void RecordFailure(SystemOneUnavailableException ex)
    {
        LogJevUnavailable(logger, ex.Failure, ex.Message);
        LastStatus = $"JEV unavailable ({ex.Message}); rule-based routing used";
        if (ex.IsPersistent)
        {
            lock (_gate)
            {
                _pausedUntil = time.GetUtcNow() + Options.PauseAfterPersistentFailure;
            }
        }
    }

    private static double Zero(double value) => double.IsNaN(value) ? 0 : value;

    private static string Show(double value) => double.IsNaN(value) ? "–" : value.ToString("0.00", CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Warning, Message = "JEV unavailable ({Failure}): {Message}; using rule-based routing")]
    private static partial void LogJevUnavailable(ILogger logger, SystemOneFailure failure, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "JEV timed out after {Elapsed} ms; using rule-based routing")]
    private static partial void LogJevTimedOut(ILogger logger, long elapsed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not persist JEV evaluations")]
    private static partial void LogPersistFailed(ILogger logger, Exception exception);
}
