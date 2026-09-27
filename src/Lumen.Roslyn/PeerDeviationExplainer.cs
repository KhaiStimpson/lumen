using Lumen.Analysis;
using Lumen.Domain;

namespace Lumen.Roslyn;

/// <summary>Template wording for peer-pattern deviations. An agent-backed explainer can replace this later.</summary>
public sealed class PeerDeviationExplainer : IReviewPointExplainer
{
    public bool CanExplain(Candidate candidate) => candidate.Facts is PeerDeviationFacts;

    public ValueTask<Explanation> ExplainAsync(Candidate candidate, AnalysisContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Explain((PeerDeviationFacts)candidate.Facts));

    public static Explanation Explain(PeerDeviationFacts f)
    {
        var role = Phrases.RolePlural(f.Role);
        var roleLower = Phrases.LowerFirst(role);
        var peers = f.Following.Count + f.NotFollowing.Count;
        var primary = f.Affected[0];
        var subjects = f.Affected.Count == 1
            ? primary.Type.Name
            : $"{f.Affected.Count} new or changed classes ({Phrases.JoinNames(f.Affected.Select(a => a.Type.Name))})";
        var verb = f.Affected.Count == 1 ? "doesn't" : "don't";
        var instead = primary.Instead.Count > 0 ? primary.Instead[0].Trait : null;

        var summary = $"{subjects} {verb} {Phrases.HasBare(f.Missing)}, unlike {f.Following.Count} of {peers} {roleLower} in this repository.";

        var why = $"Most {roleLower} here {Phrases.Has(f.Missing)} — for example {Phrases.JoinNames(f.Following.Select(p => p.Name))}. " +
                  (instead is not null
                      ? $"{primary.Type.Name} {Phrases.HasSingular(instead)} instead. "
                      : "") +
                  "Following the established approach keeps behaviour predictable for callers and makes the code easier to reason about alongside its peers. " +
                  (f.NotFollowing.Count > 0
                      ? $"There are exceptions ({Phrases.JoinNames(f.NotFollowing.Select(p => p.Name))}), so this may be deliberate — worth confirming."
                      : "No existing peer deviates from this, so a difference here is worth confirming.");

        var comment = $"Other {roleLower} in this repo ({Phrases.JoinNames(f.Following.Select(p => p.Name))}) {Phrases.Has(f.Missing)}. " +
                      $"Should {(f.Affected.Count == 1 ? primary.Type.Name : "these")} follow the same approach" +
                      (instead is not null ? $" rather than {Gerund(instead)}" : "") +
                      ", or is the difference intentional here?";

        var surface = new ComparisonSurface(
            new ComparisonSide(
                "Current approach (this PR)",
                instead is not null ? Capitalize(Phrases.HasSingular(instead)) : $"Doesn't {Phrases.HasBare(f.Missing)}",
                f.CurrentExample.Location,
                f.CurrentExample.Snippet,
                f.CurrentExample.SnippetStartLine,
                [f.CurrentExample.Location.StartLine],
                [.. f.Affected.Select(a => a.Type.Name)]),
            new ComparisonSide(
                "Established approach (precedent)",
                Capitalize(Phrases.Has(f.Missing)),
                f.BestExample.Location,
                f.BestExample.Snippet,
                f.BestExample.SnippetStartLine,
                [f.BestExample.Location.StartLine],
                [.. f.Following.Take(4).Select(p => p.Name)]));

        return new Explanation(Phrases.MissingTitle(f.Missing), summary, why, comment, surface);
    }

    private static string Gerund(Trait trait) => trait.Kind switch
    {
        TraitKind.SwallowsExceptions => "catching and handling failures locally",
        TraitKind.Throws => $"throwing {Phrases.Display(trait.Subject)}",
        TraitKind.Dependency => $"taking {Phrases.Display(trait.Subject)}",
        TraitKind.Calls => $"calling {trait.Subject}",
        _ => Phrases.HasSingular(trait),
    };

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
