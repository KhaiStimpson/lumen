using System.Text.Json;

namespace Lumen.Agents.ClaudeCode;

/// <summary>Folds <c>claude -p --output-format stream-json</c> lines into progress events and one final result.</summary>
internal sealed class ClaudeStream
{
    private const int MaxSummary = 160;

    public string? Model { get; private set; }

    /// <summary>Set when the init event reports that an API key, not the subscription login, is in use.</summary>
    public string? ApiKeySource { get; private set; }

    public bool SawResult { get; private set; }

    public bool IsError { get; private set; }

    public string? Subtype { get; private set; }

    public string? ResultText { get; private set; }

    public JsonElement? StructuredOutput { get; private set; }

    public double? CostUsd { get; private set; }

    public string? LastText { get; private set; }

    public bool UsesApiKey =>
        ApiKeySource is { Length: > 0 } source && !source.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>Consumes one line; returns the events it produced (usually zero or one).</summary>
    public IReadOnlyList<AgentEvent> Accept(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            return String(root, "type") switch
            {
                "system" => AcceptSystem(root),
                "assistant" => AcceptAssistant(root),
                "result" => AcceptResult(root),
                _ => [],
            };
        }
    }

    private List<AgentEvent> AcceptSystem(JsonElement root)
    {
        switch (String(root, "subtype"))
        {
            case "init":
                Model = String(root, "model") ?? Model;
                ApiKeySource = String(root, "apiKeySource") ?? String(root, "api_key_source");
                return [new AgentEvent(AgentEventKind.Started, Model is null ? "Started" : $"Started ({Model})")];
            case "api_retry":
                return [new AgentEvent(AgentEventKind.Retry, $"Retrying: {String(root, "error") ?? "transient error"}")];
            default:
                return [];
        }
    }

    private List<AgentEvent> AcceptAssistant(JsonElement root)
    {
        var events = new List<AgentEvent>();
        if (!root.TryGetProperty("message", out var message))
        {
            return events;
        }

        Model = String(message, "model") ?? Model;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return events;
        }

        foreach (var block in content.EnumerateArray())
        {
            switch (String(block, "type"))
            {
                case "text" when String(block, "text") is { Length: > 0 } text:
                    LastText = text;
                    events.Add(new AgentEvent(AgentEventKind.Text, Truncate(text)));
                    break;
                case "tool_use":
                    events.Add(new AgentEvent(AgentEventKind.ToolUse, DescribeTool(block)));
                    break;
            }
        }

        return events;
    }

    private List<AgentEvent> AcceptResult(JsonElement root)
    {
        SawResult = true;
        Subtype = String(root, "subtype");
        IsError = root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True
                  || (Subtype is not null && Subtype != "success");
        ResultText = String(root, "result");
        if (root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number)
        {
            CostUsd = cost.GetDouble();
        }

        if (root.TryGetProperty("structured_output", out var structured) && structured.ValueKind == JsonValueKind.Object)
        {
            StructuredOutput = structured.Clone();
        }

        return [];
    }

    private static string DescribeTool(JsonElement block)
    {
        var name = String(block, "name") ?? "tool";
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
        {
            return name;
        }

        var target = String(input, "file_path") ?? String(input, "pattern") ?? String(input, "path");
        return target is null ? name : Truncate($"{name} {target}");
    }

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Truncate(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= MaxSummary ? single : single[..(MaxSummary - 1)] + "…";
    }
}
