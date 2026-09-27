using System.Globalization;
using System.Text;
using System.Text.Json;
using Lumen.Domain;

namespace Lumen.Agents.Investigations;

/// <summary>
/// "Compare the changed implementation against analogous implementations in the repository" (TDD §11.1). Read-only:
/// it may read, grep and glob the checkout, nothing else. Its answer is checked against the repository before it
/// becomes evidence.
/// </summary>
public sealed class RepositoryPatternInvestigator(TimeProvider time) : IInvestigator
{
    public const string RoleId = "pattern-investigator/v1";

    private const int MaxFindings = 8;

    internal static readonly string OutputSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["outcome", "summary", "dominantPrecedent", "findings", "recommendReviewPoint"],
          "properties": {
            "outcome": { "type": "string", "enum": ["supported", "refuted", "inconclusive"] },
            "summary": { "type": "string", "maxLength": 600 },
            "dominantPrecedent": { "type": "string", "maxLength": 400 },
            "exceptionsExplained": { "type": "string", "maxLength": 600 },
            "whyPrecedentExists": { "type": "string", "maxLength": 600 },
            "findings": {
              "type": "array",
              "maxItems": 8,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["claim", "path", "line", "supportsReviewPoint"],
                "properties": {
                  "claim": { "type": "string", "maxLength": 300 },
                  "path": { "type": "string" },
                  "line": { "type": "integer", "minimum": 1 },
                  "supportsReviewPoint": { "type": "boolean" }
                }
              }
            },
            "recommendReviewPoint": { "type": "boolean" }
          }
        }
        """;

    private const string Instructions = """
        You are a read-only repository pattern investigator inside a code review tool. You never edit files and never
        run commands. Work only from files in the current directory. Every finding must cite a real file path
        (relative to the current directory) and a 1-based line number you actually read. Say "inconclusive" rather
        than guess. Be brief and concrete; the reviewer decides, not you.
        """;

    public InvestigationType Type => InvestigationType.RepositoryPattern;

    public bool MutatesWorkingTree => false;

    public bool CanInvestigate(ReviewPoint point) =>
        point.Type == ReviewPointType.PatternDeviation && point.Evidence.OfType<RepositoryPrecedentEvidence>().Any(e => !e.IsCounterEvidence);

    public async Task<InvestigationResult> InvestigateAsync(
        InvestigationBrief brief,
        IAgentProvider provider,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(provider);

        var limits = InvestigationLimits.For(brief.Budget);
        var session = await provider.StartSessionAsync(
            new AgentSessionRequest
            {
                Role = RoleId,
                Prompt = BuildPrompt(brief.Point),
                Instructions = Instructions,
                WorkingDirectory = workingDirectory,
                Tools = ["Read", "Grep", "Glob"],
                OutputSchema = OutputSchema,
                Timeout = limits.Timeout,
                MaxOutputBytes = limits.MaxOutputBytes,
            },
            cancellationToken).ConfigureAwait(false);

        AgentSessionResult result;
        try
        {
            result = await session.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            session.Cancel();
            throw;
        }

        var producer = $"{provider.Id} · {RoleId}{(result.Model is null ? "" : $" · {result.Model}")}";
        return Interpret(brief, result, producer, time.GetUtcNow());
    }

    internal static string BuildPrompt(ReviewPoint point)
    {
        var precedent = point.Evidence.OfType<RepositoryPrecedentEvidence>().First(e => !e.IsCounterEvidence);
        var exceptions = point.Evidence.OfType<RepositoryPrecedentEvidence>().Where(e => e.IsCounterEvidence).SelectMany(e => e.Examples).ToList();

        var prompt = new StringBuilder();
        prompt.AppendLine("A static analysis of a pull request produced this review point:");
        prompt.AppendLine(CultureInfo.InvariantCulture, $"  {point.Title}");
        prompt.AppendLine(CultureInfo.InvariantCulture, $"  {point.Summary}");
        prompt.AppendLine();
        prompt.AppendLine(CultureInfo.InvariantCulture, $"Convention: {precedent.Convention} ({precedent.Supporting} of {precedent.PeerCount} peers follow it).");
        prompt.AppendLine("Changed code that does not follow it:");
        foreach (var location in new[] { point.Anchor }.Concat(point.OtherLocations))
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"  - {location.Path}:{location.StartLine}{(location.Symbol is null ? "" : $" ({location.Symbol})")}");
        }

        prompt.AppendLine("Peers that follow it:");
        foreach (var example in precedent.Examples)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"  - {example.Location.Path}:{example.Location.StartLine} ({example.TypeName})");
        }

        if (exceptions.Count > 0)
        {
            prompt.AppendLine("Existing peers that do not follow it:");
            foreach (var example in exceptions)
            {
                prompt.AppendLine(CultureInfo.InvariantCulture, $"  - {example.Location.Path}:{example.Location.StartLine} ({example.TypeName})");
            }
        }

        prompt.AppendLine();
        prompt.AppendLine("Investigate by reading the changed code and the peers:");
        prompt.AppendLine("1. Is this a real, dominant precedent for code in the same role? State it in one sentence.");
        prompt.AppendLine("2. Do the existing exceptions share a reason that also applies to the changed code?");
        prompt.AppendLine("3. From comments or code structure, why does the precedent exist, if you can tell?");
        prompt.AppendLine("4. Does the changed code deviate in a way a reviewer should question?");
        prompt.AppendLine("outcome: \"supported\" if the review point holds, \"refuted\" if the deviation is justified or the precedent is not real, otherwise \"inconclusive\".");
        prompt.AppendLine(CultureInfo.InvariantCulture, $"Give at most {MaxFindings} findings, each citing a path and line you read.");
        return prompt.ToString();
    }

    internal static InvestigationResult Interpret(InvestigationBrief brief, AgentSessionResult result, string producer, DateTimeOffset now)
    {
        var id = $"{brief.Point.Id}-pattern-{now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
        var claim = brief.Point.Summary;

        if (!result.Succeeded || result.StructuredOutput is not { ValueKind: JsonValueKind.Object } output)
        {
            return Inconclusive(id, claim, producer, result.Error ?? "The investigation returned no structured answer.");
        }

        var evidence = new List<Evidence>();
        var counter = new List<Evidence>();
        var sources = new List<SourceReference>();
        var dropped = 0;
        if (output.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array)
        {
            foreach (var finding in findings.EnumerateArray().Take(MaxFindings))
            {
                var text = Text(finding, "claim");
                var line = finding.TryGetProperty("line", out var l) && l.TryGetInt32(out var n) ? n : 0;
                var location = Grounding.Resolve(brief.RepositoryRoot, Text(finding, "path"), line);
                if (text is null || location is null)
                {
                    dropped++;
                    continue;
                }

                var supports = !finding.TryGetProperty("supportsReviewPoint", out var s) || s.ValueKind != JsonValueKind.False;
                var item = new AgentInvestigationEvidence
                {
                    Id = $"{id}-{evidence.Count + counter.Count}",
                    Source = EvidenceSource.AgentInvestigation,
                    CreatedAt = now,
                    Summary = $"{text} ({location.Path}:{location.StartLine})",
                    Producer = producer,
                    InvestigationId = id,
                    Location = location,
                    IsCounterEvidence = !supports,
                };
                (supports ? evidence : counter).Add(item);
                sources.Add(new SourceReference(location, text));
            }
        }

        var outcome = Text(output, "outcome") switch
        {
            "supported" => InvestigationOutcome.Supported,
            "refuted" => InvestigationOutcome.Refuted,
            _ => InvestigationOutcome.Inconclusive,
        };

        // A verdict with nothing verifiable behind it is not evidence.
        if (outcome != InvestigationOutcome.Inconclusive && evidence.Count + counter.Count == 0)
        {
            outcome = InvestigationOutcome.Inconclusive;
        }

        var summary = Text(output, "summary") ?? "";
        if (dropped > 0)
        {
            summary += string.Create(CultureInfo.InvariantCulture, $" ({dropped} uncited finding{(dropped == 1 ? "" : "s")} discarded.)");
        }

        return new InvestigationResult
        {
            InvestigationId = id,
            Type = InvestigationType.RepositoryPattern,
            Claim = claim,
            Outcome = outcome,
            Summary = summary.Trim(),
            Evidence = evidence,
            CounterEvidence = counter,
            RelevantSources = sources,
            RecommendReviewPoint = outcome == InvestigationOutcome.Supported &&
                                   output.TryGetProperty("recommendReviewPoint", out var r) && r.ValueKind == JsonValueKind.True,
            Rationale = Text(output, "whyPrecedentExists"),
            Producer = producer,
        };
    }

    private static InvestigationResult Inconclusive(string id, string claim, string producer, string why) => new()
    {
        InvestigationId = id,
        Type = InvestigationType.RepositoryPattern,
        Claim = claim,
        Outcome = InvestigationOutcome.Inconclusive,
        Summary = why,
        Evidence = [],
        Producer = producer,
    };

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s
            ? s.Trim()
            : null;
}
