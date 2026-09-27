namespace Lumen.Jev;

/// <summary>
/// The bounded questions JEV answers about each candidate (TDD §10.2). Bump <see cref="SchemaVersion"/> whenever a
/// question's wording, criteria or meaning changes: persisted answers are only comparable within one version.
/// </summary>
public static class AttentionQuestions
{
    public const string SchemaVersion = "attention/v1";

    public const string Mechanical = "mechanical";
    public const string ObservableBehaviour = "observable_behaviour";
    public const string PrecedentDeviation = "precedent_deviation";
    public const string HumanJudgement = "human_judgement";
    public const string CorrectnessInvestigation = "correctness_investigation";
    public const string ArchitectureInvestigation = "architecture_investigation";
    public const string HistoryUseful = "history_useful";
    public const string EvidenceSufficient = "evidence_sufficient";

    private static readonly (string Id, string Question, string True, string False)[] Definitions =
    [
        (Mechanical, "Is this change mechanical?",
            "Generated, renamed, reformatted or otherwise mechanical; nothing a reviewer needs to reason about.",
            "Hand-written change whose content a reviewer should read."),
        (ObservableBehaviour, "Does this change observable behaviour?",
            "Callers, users or stored data could see different behaviour.",
            "Internal only; nothing observable changes."),
        (PrecedentDeviation, "Does this deviate from repository precedent?",
            "The change departs from how comparable code in this repository is consistently built.",
            "The change is consistent with repository precedent, or the precedent is too weak to matter."),
        (HumanJudgement, "Does it require human judgement?",
            "A reviewer should look and decide; the difference may be a mistake or a deliberate choice.",
            "Safe to leave without a reviewer's attention."),
        (CorrectnessInvestigation, "Does it deserve deeper correctness investigation?",
            "Worth searching for a concrete scenario in which the changed behaviour fails.",
            "Deeper correctness investigation is unlikely to find anything."),
        (ArchitectureInvestigation, "Does it require architecture investigation?",
            "May introduce a new dependency direction, layer violation or unnecessary abstraction.",
            "No architectural concern."),
        (HistoryUseful, "Is repository history likely useful?",
            "Knowing why the precedent exists (commits, past reviews) would help the reviewer decide.",
            "History would not change the reviewer's decision."),
        (EvidenceSufficient, "Is existing deterministic evidence sufficient?",
            "The static and precedent evidence already make the point; further investigation adds little.",
            "The evidence leaves a real question open that an investigation could settle."),
    ];

    public static IReadOnlyList<string> Ids { get; } = [.. Definitions.Select(d => d.Id)];

    /// <summary>Wire key for one candidate's question, e.g. "c0_mechanical".</summary>
    public static string Key(string candidateId, string questionId) => $"{candidateId}_{questionId}";

    /// <summary>Every question, once per candidate id in the state.</summary>
    public static IReadOnlyList<SystemOneQuestion> For(IEnumerable<string> candidateIds) =>
    [
        .. candidateIds.SelectMany(candidate => Definitions.Select(d => new SystemOneQuestion(
            Key(candidate, d.Id),
            SystemOneQuestionKind.Noul,
            $"About candidate \"{candidate}\" in state.candidates: {d.Question}",
            new Dictionary<string, string> { ["true"] = d.True, ["false"] = d.False }))),
    ];
}
