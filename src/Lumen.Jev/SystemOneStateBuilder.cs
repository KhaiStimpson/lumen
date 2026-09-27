using System.Text.Json.Nodes;
using Lumen.Analysis;
using Lumen.Domain;

namespace Lumen.Jev;

/// <summary>
/// The only place JEV input is assembled — the privacy chokepoint (TDD §40). By default the state holds numbers,
/// enums and categories only: no source, no paths, no identifiers. Convention wording (which names repository types)
/// is added only when the user allows code snippets to JEV.
/// </summary>
public static class SystemOneStateBuilder
{
    public static SystemOneState Build(
        AnalysisContext context,
        IReadOnlyList<(string Id, Candidate Candidate, AttentionDecision RuleDecision)> candidates,
        PrivacySettings privacy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(privacy);

        var files = context.Snapshot.Files;
        var state = new JsonObject
        {
            ["pullRequest"] = new JsonObject
            {
                ["filesChanged"] = files.Count,
                ["mechanicalFiles"] = files.Count(f => f.Mechanical.IsMechanical),
                ["testFilesChanged"] = files.Count(f => IsTestPath(f.Path)),
                ["additions"] = files.Sum(f => f.Additions),
                ["deletions"] = files.Sum(f => f.Deletions),
            },
            ["candidates"] = new JsonArray([.. candidates.Select(c => (JsonNode)Candidate(c.Id, c.Candidate, c.RuleDecision, privacy))]),
        };

        return new SystemOneState(state);
    }

    private static JsonObject Candidate(string id, Candidate candidate, AttentionDecision rule, PrivacySettings privacy)
    {
        var s = candidate.Signals;
        var unit = candidate.ChangeUnits.Count > 0 ? candidate.ChangeUnits[0] : null;
        var json = new JsonObject
        {
            ["id"] = id,
            ["reviewPointType"] = candidate.Type.ToString(),
            ["category"] = s.Category,
            ["changeKind"] = s.ChangeKind,
            ["introducedByChange"] = s.IntroducedByChange,
            ["changeUnits"] = candidate.ChangeUnits.Count,
            ["changeUnitLocations"] = unit?.Locations.Count ?? 0,
            ["repositoryPrecedent"] = new JsonObject
            {
                ["peers"] = s.PeerCount,
                ["following"] = s.Supporting,
                ["notFollowing"] = s.PeerCount - s.Supporting,
                ["support"] = Math.Round(s.Support, 2),
                ["lift"] = Math.Round(s.Lift, 2),
            },
            ["affectedLocations"] = s.AffectedLocations,
            ["evidenceItems"] = candidate.Evidence.Count(e => !e.IsCounterEvidence),
            ["counterEvidenceItems"] = candidate.Evidence.Count(e => e.IsCounterEvidence),
            ["ruleDecision"] = new JsonObject
            {
                ["action"] = rule.Action.ToString().ToLowerInvariant(),
                ["severity"] = rule.Severity.ToString().ToLowerInvariant(),
            },
        };

        if (privacy.AllowCodeSnippetsToJev)
        {
            json["convention"] = candidate.Evidence.OfType<RepositoryPrecedentEvidence>().FirstOrDefault(e => !e.IsCounterEvidence)?.Convention;
            json["changeUnitTitle"] = unit?.Title;
        }

        return json;
    }

    private static bool IsTestPath(string path) =>
        path.Split('/', '\\').Any(segment =>
            segment.Contains("test", StringComparison.OrdinalIgnoreCase) ||
            segment.Contains("spec", StringComparison.OrdinalIgnoreCase));
}
