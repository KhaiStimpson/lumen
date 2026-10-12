using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lumen.Domain;

namespace Lumen.Models.Walkthrough;

/// <summary>What the walkthrough reads: the pull request's text and its diff (or a range of it).</summary>
public sealed record WalkthroughInput
{
    public required string Title { get; init; }

    public string Description { get; init; } = "";

    public IReadOnlyList<string> CommitHeadlines { get; init; } = [];

    public required IReadOnlyList<ChangedFile> Files { get; init; }

    public required string HeadSha { get; init; }

    /// <summary>Set for "what changed since you last looked": a short walkthrough of that range only.</summary>
    public string? SinceSha { get; init; }
}

public sealed record WalkthroughOptions
{
    /// <summary>The most diff text sent for the whole pull request.</summary>
    public int MaxDiffBytes { get; init; } = 512 * 1024;

    /// <summary>The most diff text sent for one file; larger files are cut.</summary>
    public int MaxFileBytes { get; init; } = 32 * 1024;

    public int MaxConcurrent { get; init; } = 6;

    public int MaxRisks { get; init; } = 5;
}

/// <summary>
/// The PR walkthrough (plan v2 M8) as a map-reduce over the diff, so it scales to large pull requests: per-file
/// summaries in parallel (bounded), the intent alongside them, then one pass that groups and orders the files and one
/// that picks the risks. Mechanical files send only their path and size. Every model answer is checked against the
/// diff: a group or risk citing a path or line that isn't in it is dropped.
/// </summary>
public sealed class WalkthroughGenerator(IModelProvider model, WalkthroughOptions options)
{
    public const string PromptVersion = "walkthrough/v1";

    private const int MinimumFileBytes = 512;

    private static readonly string[] Categories = ["other", "concurrency", "error-handling", "security", "behaviour-change", "missing-tests"];

    /// <summary>Changes when anything that shapes the result changes, so the cache never serves a stale shape.</summary>
    public string SettingsHash =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{PromptVersion}|{model.Model}|{options.MaxDiffBytes}|{options.MaxFileBytes}|{options.MaxRisks}")))[..16];

    public string Producer => $"{ShortModel(model.Model)} · {PromptVersion}";

    /// <summary>Runs the walkthrough, reporting each partial result; returns the finished (or failed) one.</summary>
    public async Task<Domain.Walkthrough> RunAsync(WalkthroughInput input, Action<Domain.Walkthrough> update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(update);

        var since = input.SinceSha;
        var reviewable = input.Files.Where(f => !f.Mechanical.IsMechanical && !f.IsBinary).ToList();
        var state = new Domain.Walkthrough
        {
            Status = WalkthroughStatus.Running,
            Detail = "Reading the pull request…",
            Producer = Producer,
            HeadSha = input.HeadSha,
            SinceSha = since,
            FilesTotal = reviewable.Count,
        };
        var gate = new Lock();
        void Publish(Func<Domain.Walkthrough, Domain.Walkthrough> change)
        {
            Domain.Walkthrough next;
            lock (gate)
            {
                state = change(state);
                next = state;
            }

            update(next);
        }

        update(state);

        // Decide what is sent: diffs in order until the budget runs out; the rest by path and size only.
        var budget = options.MaxDiffBytes;
        var sent = new List<(ChangedFile File, string Diff)>();
        foreach (var file in reviewable)
        {
            // Too little left to say anything useful: path and size only.
            if (budget < MinimumFileBytes)
            {
                continue;
            }

            var diff = RenderDiff(file, Math.Min(options.MaxFileBytes, budget));
            if (diff.Length == 0)
            {
                continue;
            }

            budget -= Encoding.UTF8.GetByteCount(diff);
            sent.Add((file, diff));
        }

        var intentTask = IntentAsync(input, cancellationToken);
        _ = intentTask.ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully && t.Result.Length > 0)
                {
                    Publish(s => s with { Intent = t.Result });
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // Map: one summary per file, in parallel.
        var summaries = new Dictionary<string, FileSummary>(StringComparer.Ordinal);
        using var concurrency = new SemaphoreSlim(Math.Max(1, options.MaxConcurrent));
        var done = 0;
        var failures = 0;
        await Task.WhenAll(sent.Select(async item =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var summary = await SummariseFileAsync(item.File, item.Diff, since is not null, cancellationToken).ConfigureAwait(false);
                lock (summaries)
                {
                    summaries[item.File.Path] = summary;
                }
            }
            catch (ModelException) when (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref failures);
            }
            finally
            {
                concurrency.Release();
                var count = Interlocked.Increment(ref done);
                Publish(s => s with { FilesDone = count, Detail = string.Create(CultureInfo.InvariantCulture, $"Summarised {count} of {sent.Count} files…") });
            }
        })).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (sent.Count > 0 && summaries.Count == 0)
        {
            var intent = intentTask.IsCompletedSuccessfully ? intentTask.Result : "";
            var failed = state with { Status = WalkthroughStatus.Failed, Detail = "The fast model didn't answer. The review works as usual.", Intent = intent };
            update(failed);
            return failed;
        }

        // Reduce: groups and reading order, and risks, in parallel.
        var groupsTask = GroupAsync(input, summaries, cancellationToken);
        var risksTask = PickRisksAsync(summaries, cancellationToken);

        try
        {
            var (groups, mismatch) = await groupsTask.ConfigureAwait(false);
            Publish(s => s with { Groups = groups, DescriptionMismatch = mismatch, Detail = "Picking areas worth a closer look…" });
        }
        catch (ModelException) when (!cancellationToken.IsCancellationRequested)
        {
            Publish(s => s with { Groups = FallbackGroups(input.Files, summaries) });
        }

        IReadOnlyList<WalkthroughRisk> risks;
        try
        {
            risks = await risksTask.ConfigureAwait(false);
        }
        catch (ModelException) when (!cancellationToken.IsCancellationRequested)
        {
            risks = [];
        }

        string finalIntent;
        try
        {
            finalIntent = await intentTask.ConfigureAwait(false);
        }
        catch (ModelException) when (!cancellationToken.IsCancellationRequested)
        {
            finalIntent = "";
        }

        var skipped = reviewable.Count - sent.Count;
        var detail = (skipped, failures) switch
        {
            (0, 0) => "",
            (> 0, 0) => string.Create(CultureInfo.InvariantCulture, $"{skipped} large file{(skipped == 1 ? " was" : "s were")} summarised by path and size only."),
            _ => string.Create(CultureInfo.InvariantCulture, $"{failures} file{(failures == 1 ? "" : "s")} couldn't be summarised."),
        };
        Domain.Walkthrough complete;
        lock (gate)
        {
            complete = state with
            {
                Status = WalkthroughStatus.Complete,
                Intent = finalIntent.Length > 0 ? finalIntent : state.Intent,
                Risks = risks,
                Detail = detail,
                FilesDone = sent.Count,
            };
            state = complete;
        }

        update(complete);
        return complete;
    }

    // ---------------------------------------------------------------- Map

    internal sealed record FileSummary(ChangedFile File, string Summary, string Kind, IReadOnlyList<(int Line, RiskCategory Category, string Reason)> Risks);

    private const string FileSchema = """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string", "description": "What changed in this file, in at most 25 words." },
            "kind": { "type": "string", "enum": ["logic", "test", "config", "docs", "mechanical", "other"] },
            "risks": {
              "type": "array",
              "maxItems": 3,
              "items": {
                "type": "object",
                "properties": {
                  "line": { "type": "integer", "description": "A head-side line number shown in the diff." },
                  "category": { "type": "string", "enum": ["other", "concurrency", "error-handling", "security", "behaviour-change", "missing-tests"] },
                  "reason": { "type": "string", "description": "Why a reviewer should look here, in at most 20 words." }
                },
                "required": ["line", "category", "reason"],
                "additionalProperties": false
              }
            }
          },
          "required": ["summary", "kind", "risks"],
          "additionalProperties": false
        }
        """;

    private async Task<FileSummary> SummariseFileAsync(ChangedFile file, string diff, bool isDelta, CancellationToken ct)
    {
        var completion = await model.CompleteAsync(
            new ModelRequest
            {
                Purpose = "walkthrough/file",
                System = """
                    You help a code reviewer read a pull request. You get one file's diff. Lines are shown as
                    "+ 42| code" (added, head line 42), "  43| code" (unchanged) and "-   | code" (removed).
                    Describe what changed, factually and briefly; do not praise or judge style.
                    List at most three places that deserve a careful look (concurrency, error handling, security,
                    a behaviour change, or logic without tests), citing a head-side line number that appears in the
                    diff. Only list real concerns; an empty list is fine. Answer with JSON only.
                    """,
                User = (isDelta ? "These are the changes since the reviewer last looked.\n\n" : "") + diff,
                SchemaName = "file_summary",
                JsonSchema = FileSchema,
                MaxOutputTokens = 600,
            },
            null,
            ct).ConfigureAwait(false);

        var json = ParseJson(completion.Text);
        var risks = (json?["risks"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(r => (Line: Int(r["line"]), Category: Category(Str(r["category"])), Reason: Str(r["reason"]).Trim()))
            .Where(r => r.Line > 0 && r.Reason.Length > 0)
            .Take(3)
            .ToList();
        return new FileSummary(file, Str(json?["summary"]).Trim(), Str(json?["kind"]), risks);
    }

    // ---------------------------------------------------------------- Intent

    private const string IntentSchema = """
        {
          "type": "object",
          "properties": { "intent": { "type": "string", "description": "Two or three sentences." } },
          "required": ["intent"],
          "additionalProperties": false
        }
        """;

    private async Task<string> IntentAsync(WalkthroughInput input, CancellationToken ct)
    {
        var text = new StringBuilder();
        text.Append("Title: ").AppendLine(input.Title);
        if (input.Description.Length > 0)
        {
            text.AppendLine().AppendLine("Description:").AppendLine(Cut(input.Description, 4000));
        }

        if (input.CommitHeadlines.Count > 0)
        {
            text.AppendLine().AppendLine("Commits:");
            foreach (var headline in input.CommitHeadlines.Take(30))
            {
                text.Append("- ").AppendLine(headline);
            }
        }

        text.AppendLine().AppendLine("Files:");
        foreach (var file in input.Files.Take(200))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {file.Path} (+{file.Additions} −{file.Deletions}){(file.Mechanical.IsMechanical ? " [generated]" : "")}");
        }

        var completion = await model.CompleteAsync(
            new ModelRequest
            {
                Purpose = "walkthrough/intent",
                System = input.SinceSha is null
                    ? "You help a code reviewer. In two or three plain sentences, say what this pull request is trying to do. Answer with JSON only."
                    : "You help a code reviewer who already reviewed this pull request. In one or two plain sentences, say what changed since they last looked. Answer with JSON only.",
                User = text.ToString(),
                SchemaName = "intent",
                JsonSchema = IntentSchema,
                MaxOutputTokens = 300,
            },
            null,
            ct).ConfigureAwait(false);
        return Str(ParseJson(completion.Text)?["intent"]).Trim();
    }

    // ---------------------------------------------------------------- Reduce: groups

    private const string GroupSchema = """
        {
          "type": "object",
          "properties": {
            "groups": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string", "description": "A short name for the change, e.g. \"EF migration\"." },
                  "summary": { "type": "string", "description": "One line." },
                  "paths": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["title", "summary", "paths"],
                "additionalProperties": false
              }
            },
            "descriptionMismatch": { "type": "string", "description": "Where the description and the diff disagree; empty if they agree." }
          },
          "required": ["groups", "descriptionMismatch"],
          "additionalProperties": false
        }
        """;

    private async Task<(IReadOnlyList<WalkthroughGroup> Groups, string Mismatch)> GroupAsync(
        WalkthroughInput input,
        IReadOnlyDictionary<string, FileSummary> summaries,
        CancellationToken ct)
    {
        var text = new StringBuilder();
        text.Append("Title: ").AppendLine(input.Title);
        if (input.Description.Length > 0)
        {
            text.AppendLine().AppendLine("Description:").AppendLine(Cut(input.Description, 3000));
        }

        text.AppendLine().AppendLine("Changed files:");
        foreach (var file in input.Files.Where(f => !f.Mechanical.IsMechanical))
        {
            var summary = summaries.GetValueOrDefault(file.Path)?.Summary;
            text.AppendLine(CultureInfo.InvariantCulture, $"- {file.Path} (+{file.Additions} −{file.Deletions}): {(string.IsNullOrEmpty(summary) ? "(not summarised)" : summary)}");
        }

        var completion = await model.CompleteAsync(
            new ModelRequest
            {
                Purpose = "walkthrough/groups",
                System = """
                    You help a code reviewer plan how to read a pull request. Group the changed files into logical
                    changes (for example "New enrichment providers", "EF migration", "DI wiring"), each with a one-line
                    summary. Every path must be one of the listed paths, and each path belongs to one group.
                    List the groups in reading order: what others depend on first (models, contracts, data), then
                    the code that uses it, then wiring and configuration, and tests last.
                    Also say briefly where the description claims something the files don't show, or the files do
                    something the description doesn't mention; leave it empty if they agree. Answer with JSON only.
                    """,
                User = text.ToString(),
                SchemaName = "groups",
                JsonSchema = GroupSchema,
                MaxOutputTokens = 2000,
            },
            null,
            ct).ConfigureAwait(false);

        var json = ParseJson(completion.Text);
        var raw = (json?["groups"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(g => (Title: Str(g["title"]).Trim(), Summary: Str(g["summary"]).Trim(), Paths: (g["paths"] as JsonArray ?? []).Select(Str).ToList()))
            .ToList();
        return (WalkthroughGrounding.GroundGroups(raw, input.Files, summaries.ToDictionary(s => s.Key, s => s.Value.Kind)), Str(json?["descriptionMismatch"]).Trim());
    }

    private static IReadOnlyList<WalkthroughGroup> FallbackGroups(IReadOnlyList<ChangedFile> files, IReadOnlyDictionary<string, FileSummary> summaries) =>
        WalkthroughGrounding.GroundGroups([], files, summaries.ToDictionary(s => s.Key, s => s.Value.Kind));

    // ---------------------------------------------------------------- Reduce: risks

    private const string RiskSchema = """
        {
          "type": "object",
          "properties": {
            "picks": {
              "type": "array",
              "maxItems": 5,
              "items": {
                "type": "object",
                "properties": {
                  "candidate": { "type": "integer" },
                  "reason": { "type": "string", "description": "At most 20 words." }
                },
                "required": ["candidate", "reason"],
                "additionalProperties": false
              }
            }
          },
          "required": ["picks"],
          "additionalProperties": false
        }
        """;

    private async Task<IReadOnlyList<WalkthroughRisk>> PickRisksAsync(IReadOnlyDictionary<string, FileSummary> summaries, CancellationToken ct)
    {
        var candidates = summaries.Values
            .OrderBy(s => s.File.Path, StringComparer.Ordinal)
            .SelectMany(s => s.Risks.Select(r => (s.File.Path, r.Line, r.Category, r.Reason)))
            .ToList();
        candidates = [.. WalkthroughGrounding.GroundRisks(candidates, summaries.Values.Select(s => s.File).ToList(), int.MaxValue)
            .Select(r => (r.Location.Path, r.Location.StartLine, r.Category, r.Reason))];
        if (candidates.Count <= options.MaxRisks)
        {
            return WalkthroughGrounding.GroundRisks(candidates, [.. summaries.Values.Select(s => s.File)], options.MaxRisks);
        }

        var text = new StringBuilder("Candidates:\n");
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            text.AppendLine(CultureInfo.InvariantCulture, $"{i}. {c.Path}:{c.Line} [{Categories[(int)c.Category]}] {c.Reason}");
        }

        var completion = await model.CompleteAsync(
            new ModelRequest
            {
                Purpose = "walkthrough/risks",
                System = string.Create(
                    CultureInfo.InvariantCulture,
                    $"""
                    You help a code reviewer decide where to look first. From the numbered candidates, pick at most
                    {options.MaxRisks} that most deserve careful human review (real correctness, concurrency, security
                    or behaviour risk beats style). Keep or tighten each reason. Answer with JSON only.
                    """),
                User = text.ToString(),
                SchemaName = "risks",
                JsonSchema = RiskSchema,
                MaxOutputTokens = 600,
            },
            null,
            ct).ConfigureAwait(false);

        var picks = (ParseJson(completion.Text)?["picks"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(p => (Index: Int(p["candidate"]), Reason: Str(p["reason"]).Trim()))
            .Where(p => p.Index >= 0 && p.Index < candidates.Count)
            .DistinctBy(p => p.Index)
            .Select(p => candidates[p.Index] with { Reason = p.Reason.Length > 0 ? p.Reason : candidates[p.Index].Reason })
            .ToList();
        return WalkthroughGrounding.GroundRisks(picks, [.. summaries.Values.Select(s => s.File)], options.MaxRisks);
    }

    // ---------------------------------------------------------------- Helpers

    /// <summary>The diff as the model reads it: head line numbers on added and unchanged lines.</summary>
    internal static string RenderDiff(ChangedFile file, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return "";
        }

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"File: {file.Path} ({file.Kind}, +{file.Additions} −{file.Deletions})");
        if (file.OldPath is { } old && old != file.Path)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Renamed from: {old}");
        }

        foreach (var hunk in file.Hunks)
        {
            text.AppendLine(hunk.Header);
            foreach (var line in hunk.Lines)
            {
                var prefix = line.Kind switch
                {
                    DiffLineKind.Added => string.Create(CultureInfo.InvariantCulture, $"+{line.NewNumber,5}| "),
                    DiffLineKind.Removed => "-     | ",
                    _ => string.Create(CultureInfo.InvariantCulture, $" {line.NewNumber,5}| "),
                };
                text.Append(prefix).AppendLine(line.Text);
                if (text.Length > maxBytes)
                {
                    text.AppendLine("… (the rest of this file's diff was cut)");
                    return text.ToString();
                }
            }
        }

        return text.ToString();
    }

    /// <summary>Tolerates code fences or prose around the JSON object.</summary>
    internal static JsonObject? ParseJson(string text)
    {
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text[start..(end + 1)]) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static RiskCategory Category(string value) => Array.IndexOf(Categories, value) is var i and >= 0 ? (RiskCategory)i : RiskCategory.Other;

    private static string ShortModel(string model) => model.Contains('/', StringComparison.Ordinal) ? model[(model.LastIndexOf('/') + 1)..] : model;

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static int Int(JsonNode? node) =>
        node is JsonValue v && (v.TryGetValue<int>(out var i) || (v.TryGetValue<double>(out var d) && (i = (int)d) == d)) ? i : -1;
}

/// <summary>Keeps a model's answer inside the diff (TDD §2.3: the model's reading, never invented locations).</summary>
public static class WalkthroughGrounding
{
    /// <summary>
    /// Drops paths that aren't in the diff and empty groups, gives each path one group, puts unlisted files in "Other
    /// changes", and mechanical files in their own collapsed group at the end.
    /// </summary>
    public static IReadOnlyList<WalkthroughGroup> GroundGroups(
        IReadOnlyList<(string Title, string Summary, List<string> Paths)> raw,
        IReadOnlyList<ChangedFile> files,
        IReadOnlyDictionary<string, string>? kinds = null)
    {
        var byPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        bool IsMechanical(string path) =>
            byPath[path].Mechanical.IsMechanical || (kinds?.GetValueOrDefault(path) == "mechanical");
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<WalkthroughGroup>();

        foreach (var (title, summary, paths) in raw)
        {
            var kept = paths.Select(p => p.Trim()).Where(p => byPath.ContainsKey(p) && !IsMechanical(p) && placed.Add(p)).ToList();
            if (kept.Count > 0 && title.Length > 0)
            {
                groups.Add(new WalkthroughGroup("", title, summary, kept, 0, false));
            }
        }

        var rest = files.Where(f => !placed.Contains(f.Path) && !IsMechanical(f.Path)).Select(f => f.Path).ToList();
        if (rest.Count > 0)
        {
            groups.Add(new WalkthroughGroup("", raw.Count == 0 ? "Changes" : "Other changes", "", rest, 0, false));
        }

        var mechanical = files.Where(f => IsMechanical(f.Path)).Select(f => f.Path).ToList();
        if (mechanical.Count > 0)
        {
            groups.Add(new WalkthroughGroup(
                "",
                "Generated and mechanical",
                string.Create(CultureInfo.InvariantCulture, $"{mechanical.Count} file{(mechanical.Count == 1 ? "" : "s")} sent by path and size only"),
                mechanical,
                0,
                true));
        }

        return [.. groups.Select((g, i) => g with { Id = string.Create(CultureInfo.InvariantCulture, $"g{i + 1}"), Order = i + 1 })];
    }

    /// <summary>Keeps risks whose path is in the diff and whose line is a head-side line the diff shows.</summary>
    public static IReadOnlyList<WalkthroughRisk> GroundRisks(
        IReadOnlyList<(string Path, int Line, RiskCategory Category, string Reason)> raw,
        IReadOnlyList<ChangedFile> files,
        int max)
    {
        var byPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        return
        [
            .. raw
                .Where(r => byPath.TryGetValue(r.Path, out var file) && file.ContainsNewLine(r.Line) && r.Reason.Length > 0)
                .DistinctBy(r => (r.Path, r.Line))
                .Take(max)
                .Select(r => new WalkthroughRisk(
                    "risk-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{r.Path}:{r.Line}:{r.Category}"))))[..12].ToLowerInvariant(),
                    new CodeLocation(r.Path, r.Line, r.Line),
                    r.Category,
                    r.Reason)),
        ];
    }
}
