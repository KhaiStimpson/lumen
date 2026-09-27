using Lumen.Domain;

namespace Lumen.Agents.Investigations;

/// <summary>What an investigation starts from: a surfaced review point, its deterministic evidence and the checkout.</summary>
public sealed record InvestigationBrief(ReviewPoint Point, string RepositoryRoot, InvestigationBudget Budget);

/// <summary>Concrete limits for a budget (TDD §36). Budgets are enforced through wall-clock time and output size.</summary>
public sealed record InvestigationLimits(TimeSpan Timeout, long MaxOutputBytes, bool IsolatedWorktree)
{
    public static InvestigationLimits For(InvestigationBudget budget) => budget switch
    {
        InvestigationBudget.Tiny => new(TimeSpan.FromSeconds(90), 2 * 1024 * 1024, IsolatedWorktree: false),
        InvestigationBudget.Standard => new(TimeSpan.FromMinutes(4), 8 * 1024 * 1024, IsolatedWorktree: false),
        _ => new(TimeSpan.FromMinutes(10), 16 * 1024 * 1024, IsolatedWorktree: true),
    };
}

/// <summary>One specialist role (TDD §11.1). Returns a structured result; never UI (§13).</summary>
public interface IInvestigator
{
    InvestigationType Type { get; }

    /// <summary>True when the role may modify files and so needs an isolated worktree (§12).</summary>
    bool MutatesWorkingTree { get; }

    bool CanInvestigate(ReviewPoint point);

    Task<InvestigationResult> InvestigateAsync(
        InvestigationBrief brief,
        IAgentProvider provider,
        string workingDirectory,
        CancellationToken cancellationToken);
}

/// <summary>
/// Keeps agent citations honest: a finding counts as evidence only if it names a real file inside the repository
/// and a line that exists in it.
/// </summary>
public static class Grounding
{
    public static CodeLocation? Resolve(string repositoryRoot, string? path, int line)
    {
        if (string.IsNullOrWhiteSpace(path) || line < 1)
        {
            return null;
        }

        var relative = path.Replace('\\', '/').TrimStart('/');
        if (relative.StartsWith("./", StringComparison.Ordinal))
        {
            relative = relative[2..];
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, relative));
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            return null;
        }

        var lines = File.ReadLines(full).Count();
        return line <= lines ? new CodeLocation(Path.GetRelativePath(root, full).Replace('\\', '/'), line, line) : null;
    }
}
