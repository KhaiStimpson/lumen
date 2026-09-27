namespace Lumen.Domain;

/// <summary>What may leave the machine (TDD §40). Defaults send no source code anywhere.</summary>
public sealed record PrivacySettings
{
    /// <summary>Any outbound model call at all (JEV or agents).</summary>
    public bool AllowCloudReasoning { get; init; } = true;

    /// <summary>Code snippets or identifiers to JEV. JEV receives only numbers and categories otherwise.</summary>
    public bool AllowCodeSnippetsToJev { get; init; }

    /// <summary>Repository code to Claude/OpenAI coding agents. Investigations cannot run without it.</summary>
    public bool AllowCodeToAgents { get; init; }

    public bool LocalModelsOnly { get; init; }

    public bool AllowsJev => AllowCloudReasoning && !LocalModelsOnly;

    public bool AllowsAgents => AllowCloudReasoning && !LocalModelsOnly && AllowCodeToAgents;
}

/// <summary>
/// The platform secure credential store (TDD §39A): Windows Credential Manager, later macOS Keychain and Secret
/// Service. Credentials never go to SQLite or settings files.
/// </summary>
public interface ISecretStore
{
    bool IsAvailable { get; }

    string? Read(string name);

    void Write(string name, string secret);

    bool Delete(string name);
}

public static class SecretNames
{
    public const string OpenRouterApiKey = "Lumen/OpenRouter";
}
