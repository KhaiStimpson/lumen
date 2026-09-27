using System.Text;
using Lumen.Agents.ClaudeCode;
using Lumen.Agents.Execution;
using Lumen.Domain;
using Lumen.Jev;
using Lumen.Storage;

namespace Lumen.Engine;

/// <summary>
/// <c>Lumen.Engine.exe connections [status|set-openrouter-key|remove-openrouter-key]</c> (TDD §39). Nothing here spends
/// usage: the key check and Claude Code's sign-in status are both free. Keys are read from stdin, never from argv.
/// </summary>
public static class ConnectionsCommand
{
    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = EngineOptions.FromArgs(args);
        var secrets = OperatingSystem.IsWindows() ? (ISecretStore)new WindowsCredentialStore() : new UnavailableSecretStore();
        var verb = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "status";

        switch (verb)
        {
            case "set-openrouter-key":
                output.Write("OpenRouter API key (input is not echoed to history; paste and press Enter): ");
                var key = ReadSecret(input)?.Trim();
                if (string.IsNullOrEmpty(key))
                {
                    output.WriteLine("No key entered; nothing changed.");
                    return 1;
                }

                secrets.Write(SecretNames.OpenRouterApiKey, key);
                output.WriteLine($"Stored in the credential store as '{SecretNames.OpenRouterApiKey}'.");
                await WriteStatusAsync(options, secrets, output).ConfigureAwait(false);
                return 0;

            case "remove-openrouter-key":
                output.WriteLine(secrets.Delete(SecretNames.OpenRouterApiKey) ? "OpenRouter key removed." : "No OpenRouter key was stored.");
                return 0;

            case "status":
                await WriteStatusAsync(options, secrets, output).ConfigureAwait(false);
                return 0;

            default:
                output.WriteLine("Usage: Lumen.Engine connections [status|set-openrouter-key|remove-openrouter-key] [--data-dir <dir>]");
                return 2;
        }
    }

    private static async Task WriteStatusAsync(EngineOptions options, ISecretStore secrets, TextWriter output)
    {
        var settings = EngineSettings.Load(options.DataDirectory);
        output.WriteLine($"Settings: {EngineSettings.PathIn(options.DataDirectory)}");
        output.WriteLine();

        using var http = new HttpClient();
        var jev = new OpenRouterSystemOneEvaluator(http, secrets, new OpenRouterOptions { Model = settings.Jev.Model });
        output.WriteLine("JEV");
        if (!settings.Jev.Enabled || !settings.Privacy.AllowsJev)
        {
            output.WriteLine("  ○ Off (settings: jev.enabled / privacy.allowCloudReasoning)");
        }
        else if (!jev.IsConfigured)
        {
            output.WriteLine("  ○ No OpenRouter key — run: Lumen.Engine connections set-openrouter-key");
        }
        else
        {
            var key = await jev.CheckKeyAsync(CancellationToken.None).ConfigureAwait(false);
            output.WriteLine($"  {(key.Valid ? "●" : "✕")} OpenRouter API (metered) · {settings.Jev.Model} · {key.Detail}");
        }

        output.WriteLine($"  Sends: {(settings.Privacy.AllowCodeSnippetsToJev ? "counts, categories and convention wording" : "counts and categories only — no code, paths or names")}");
        output.WriteLine();

        var runner = new SandboxedProcessRunner(new CommandPolicy
        {
            AllowedExecutables = new HashSet<string>(StringComparer.Ordinal) { ClaudeCodeProvider.Executable },
            AllowedWorkingRoots = [options.DataDirectory],
        });
        var claude = await new ClaudeCodeProvider(runner, Path.Combine(options.DataDirectory, "agent-state"))
            .GetAuthenticationStateAsync(CancellationToken.None).ConfigureAwait(false);
        output.WriteLine("Claude");
        output.WriteLine($"  {(claude.IsUsable ? "●" : "○")} {claude.Detail}{(claude.Version is null ? "" : $" · {claude.Version}")}");
        output.WriteLine(settings.AgentsAllowed
            ? $"  Investigations: on · up to {settings.Agents.MaxInvestigationsPerPullRequest} per pull request · {(settings.Agents.AllowMeteredUsage ? "metered usage ALLOWED" : "subscription only")}"
            : "  Investigations: off (settings: agents.enabled and privacy.allowCodeToAgents must both be true)");
    }

    /// <summary>Reads without echo when attached to a console; falls back to a plain line for redirected input.</summary>
    private static string? ReadSecret(TextReader input)
    {
        if (!ReferenceEquals(input, Console.In) || Console.IsInputRedirected)
        {
            return input.ReadLine();
        }

        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace && buffer.Length > 0)
            {
                buffer.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }
}
