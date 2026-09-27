using Lumen.Domain;
using Microsoft.Extensions.Logging;

namespace Lumen.Agents.Investigations;

/// <summary>One requested investigation (TDD §35). Higher priority runs first.</summary>
public sealed record InvestigationJob(ReviewPoint Point, InvestigationType Type, InvestigationBudget Budget, double Priority);

public sealed record InvestigationSchedulerOptions
{
    /// <summary>Agent sessions running at once across all pull requests.</summary>
    public int MaxConcurrent { get; init; } = 1;

    /// <summary>New (non-cached) investigations per pull request head; cached results are always reused.</summary>
    public int MaxPerPullRequest { get; init; } = 3;

    /// <summary>Only an explicit user opt-in allows metered billing (TDD §37.5).</summary>
    public bool AllowMeteredUsage { get; init; }
}

public sealed record InvestigationRunSummary(int Cached, int Completed, int Skipped, string? Unavailable);

/// <summary>
/// Runs investigations for one pull request: cached results first (free), then new sessions in priority order within
/// the per-PR cap and a global concurrency limit. Results go to the caller; the engine decides what they change.
/// </summary>
public sealed partial class InvestigationScheduler : IDisposable
{
    private readonly IReadOnlyList<IInvestigator> _investigators;
    private readonly IAgentProvider _provider;
    private readonly IInvestigationStore _store;
    private readonly IAgentWorktreeFactory _worktrees;
    private readonly Func<InvestigationSchedulerOptions> _currentOptions;
    private readonly ILogger<InvestigationScheduler> _logger;

    // A counting gate rather than a SemaphoreSlim, so a change to MaxConcurrent applies without a restart.
    private readonly Lock _gate = new();
    private readonly Queue<TaskCompletionSource> _waiting = new();
    private int _running;

    public InvestigationScheduler(
        IEnumerable<IInvestigator> investigators,
        IAgentProvider provider,
        IInvestigationStore store,
        IAgentWorktreeFactory worktrees,
        InvestigationSchedulerOptions options,
        ILogger<InvestigationScheduler> logger)
        : this(investigators, provider, store, worktrees, () => options, logger)
    {
    }

    /// <param name="currentOptions">Read at each use, so Settings changes apply to the next investigation.</param>
    public InvestigationScheduler(
        IEnumerable<IInvestigator> investigators,
        IAgentProvider provider,
        IInvestigationStore store,
        IAgentWorktreeFactory worktrees,
        Func<InvestigationSchedulerOptions> currentOptions,
        ILogger<InvestigationScheduler> logger)
    {
        _investigators = [.. investigators];
        _provider = provider;
        _store = store;
        _worktrees = worktrees;
        _currentOptions = currentOptions;
        _logger = logger;
    }

    public IInvestigator? Find(ReviewPoint point, InvestigationType type) =>
        _investigators.FirstOrDefault(i => i.Type == type && i.CanInvestigate(point));

    public async Task<InvestigationRunSummary> RunAsync(
        IPullRequestCheckout checkout,
        PullRequestKey key,
        IReadOnlyList<InvestigationJob> jobs,
        Func<InvestigationResult, ReviewPoint, Task> onResult,
        Action<int> onActiveChanged,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkout);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(onResult);
        ArgumentNullException.ThrowIfNull(onActiveChanged);

        var runnable = jobs
            .Select(job => (Job: job, Investigator: Find(job.Point, job.Type)))
            .Where(j => j.Investigator is not null)
            .DistinctBy(j => (j.Job.Point.Id, j.Job.Type))
            .OrderByDescending(j => j.Job.Priority)
            .ToList();

        var fresh = new List<(InvestigationJob Job, IInvestigator Investigator)>();
        var cached = 0;
        foreach (var (job, investigator) in runnable)
        {
            var previous = await _store.FindInvestigationAsync(key, checkout.HeadSha, job.Point.Id, job.Type, cancellationToken).ConfigureAwait(false);
            if (previous is not null)
            {
                cached++;
                await onResult(previous, job.Point).ConfigureAwait(false);
            }
            else
            {
                fresh.Add((job, investigator!));
            }
        }

        var allowance = Math.Max(0, _currentOptions().MaxPerPullRequest - await _store.CountInvestigationsAsync(key, checkout.HeadSha, cancellationToken).ConfigureAwait(false));
        var skipped = Math.Max(0, fresh.Count - allowance);
        fresh = [.. fresh.Take(allowance)];
        if (fresh.Count == 0)
        {
            return new InvestigationRunSummary(cached, 0, skipped, null);
        }

        var auth = await _provider.GetAuthenticationStateAsync(cancellationToken).ConfigureAwait(false);
        if (!auth.IsUsable || (auth.Billing != AgentBilling.Subscription && !_currentOptions().AllowMeteredUsage))
        {
            var reason = auth.IsUsable ? $"{auth.Detail}; metered usage is not enabled" : auth.Detail;
            LogProviderUnavailable(_logger, _provider.Id, reason);
            return new InvestigationRunSummary(cached, 0, skipped + fresh.Count, reason);
        }

        var remaining = fresh.Count;
        var completed = 0;
        string? unavailable = null;
        onActiveChanged(remaining);

        await Task.WhenAll(fresh.Select(async item =>
        {
            await EnterAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (unavailable is not null)
                {
                    return;
                }

                var result = await RunOneAsync(checkout, item.Job, item.Investigator, cancellationToken).ConfigureAwait(false);
                await _store.SaveInvestigationAsync(key, checkout.HeadSha, item.Job.Point.Id, result, CancellationToken.None).ConfigureAwait(false);
                Interlocked.Increment(ref completed);
                await onResult(result, item.Job.Point).ConfigureAwait(false);
            }
            catch (AgentUnavailableException ex)
            {
                unavailable = ex.Message;
                LogProviderUnavailable(_logger, _provider.Id, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogInvestigationFailed(_logger, ex, item.Job.Type, item.Job.Point.Id);
            }
            finally
            {
                Exit();
                onActiveChanged(Interlocked.Decrement(ref remaining));
            }
        })).ConfigureAwait(false);

        return new InvestigationRunSummary(cached, completed, skipped + fresh.Count - completed, unavailable);
    }

    private async Task<InvestigationResult> RunOneAsync(
        IPullRequestCheckout checkout,
        InvestigationJob job,
        IInvestigator investigator,
        CancellationToken cancellationToken)
    {
        var brief = new InvestigationBrief(job.Point, checkout.RootPath, job.Budget);
        if (!investigator.MutatesWorkingTree && !InvestigationLimits.For(job.Budget).IsolatedWorktree)
        {
            return await investigator.InvestigateAsync(brief, _provider, checkout.RootPath, cancellationToken).ConfigureAwait(false);
        }

        var worktree = await _worktrees.CreateAsync(checkout, $"{job.Type.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}"[..24], cancellationToken).ConfigureAwait(false);
        await using (worktree.ConfigureAwait(false))
        {
            return await investigator.InvestigateAsync(brief with { RepositoryRoot = worktree.Path }, _provider, worktree.Path, cancellationToken).ConfigureAwait(false);
        }
    }

    private int MaxConcurrent => Math.Max(1, _currentOptions().MaxConcurrent);

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource turn;
        lock (_gate)
        {
            if (_running < MaxConcurrent)
            {
                _running++;
                return;
            }

            turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Enqueue(turn);
        }

        using (cancellationToken.Register(() => turn.TrySetCanceled(cancellationToken)))
        {
            await turn.Task.ConfigureAwait(false);
        }
    }

    private void Exit()
    {
        lock (_gate)
        {
            _running--;

            // A cancelled waiter can't take its turn; skip it and hand the slot on.
            while (_running < MaxConcurrent && _waiting.TryDequeue(out var next))
            {
                if (next.TrySetResult())
                {
                    _running++;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            while (_waiting.TryDequeue(out var waiter))
            {
                waiter.TrySetCanceled();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent provider {Provider} unavailable: {Reason}; showing deterministic evidence only")]
    private static partial void LogProviderUnavailable(ILogger logger, string provider, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Type} investigation of {ReviewPoint} failed")]
    private static partial void LogInvestigationFailed(ILogger logger, Exception exception, InvestigationType type, string reviewPoint);
}
