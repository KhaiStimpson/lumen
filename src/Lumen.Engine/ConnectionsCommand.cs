using System.Text;
using Lumen.Agents.ClaudeCode;
using Lumen.Agents.Execution;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Jev;
using Lumen.Storage;

namespace Lumen.Engine;

/// <summary>
/// <c>Lumen.Engine.exe connections [status|set-cloudflare|remove-cloudflare|set-openrouter-key|remove-openrouter-key]</c> (TDD §39). Nothing here spends
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

            case "set-cloudflare":
                output.Write("Cloudflare account id: ");
                var account = input.ReadLine()?.Trim();
                output.Write("Cloudflare API token with Workers AI access (paste and press Enter): ");
                var token = ReadSecret(input)?.Trim();
                if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(token))
                {
                    output.WriteLine("Both are needed; nothing changed.");
                    return 1;
                }

                secrets.Write(SecretNames.CloudflareAccountId, account);
                secrets.Write(SecretNames.CloudflareApiToken, token);
                output.WriteLine("Stored in the credential store.");
                await WriteStatusAsync(options, secrets, output).ConfigureAwait(false);
                return 0;

            case "remove-cloudflare":
                secrets.Delete(SecretNames.CloudflareAccountId);
                output.WriteLine(secrets.Delete(SecretNames.CloudflareApiToken) ? "Cloudflare credentials removed." : "No Cloudflare credentials were stored.");
                return 0;

            case "remove-openrouter-key":
                output.WriteLine(secrets.Delete(SecretNames.OpenRouterApiKey) ? "OpenRouter key removed." : "No OpenRouter key was stored.");
                return 0;

            case "status":
                await WriteStatusAsync(options, secrets, output).ConfigureAwait(false);
                return 0;

            default:
                output.WriteLine("Usage: Lumen.Engine connections [status|set-cloudflare|remove-cloudflare|set-openrouter-key|remove-openrouter-key] [--data-dir <dir>]");
                return 2;
        }
    }

    private static async Task WriteStatusAsync(EngineOptions options, ISecretStore secrets, TextWriter output)
    {
        var settings = EngineSettings.Load(options.DataDirectory);
        using var http = new HttpClient();
        var runner = new SandboxedProcessRunner(new CommandPolicy
        {
            AllowedExecutables = new HashSet<string>(StringComparer.Ordinal) { ClaudeCodeProvider.Executable },
            AllowedWorkingRoots = [options.DataDirectory],
        });
        var probe = new ConnectionsProbe(
            new EngineSettingsStore(settings, options.DataDirectory),
            options.DataDirectory,
            secrets,
            new OpenRouterSystemOneEvaluator(http, secrets, new OpenRouterOptions { Model = settings.Jev.Model }),
            new ClaudeCodeProvider(runner, Path.Combine(options.DataDirectory, "agent-state")),
            new Models.Cloudflare.CloudflareWorkersAiProvider(
                http,
                secrets,
                () => new Models.Cloudflare.CloudflareOptions { Model = settings.FastModel.Model, Gateway = settings.FastModel.Gateway },
                new Models.UnlimitedUsageMeter()),
            new Walkthroughs.DailyUsageMeter(options.DataDirectory, () => settings.FastModel.DailyRequestCap, TimeProvider.System));
        var status = await probe.GetAsync(CancellationToken.None).ConfigureAwait(false);

        output.WriteLine($"Settings: {status.SettingsPath}");
        output.WriteLine();

        var fast = status.FastModel;
        output.WriteLine("Fast model (walkthrough)");
        output.WriteLine(fast.State switch
        {
            ConnectionState.Connected => $"  ● Cloudflare Workers AI · {fast.Model} · {fast.Detail}",
            ConnectionState.Error => $"  ✕ Cloudflare Workers AI · {fast.Model} · {fast.Detail}",
            ConnectionState.NotConnected when fast.CanStoreCredentials => $"  ○ {fast.Detail} — run: Lumen.Engine connections set-cloudflare",
            _ => $"  ○ {fast.Detail}",
        });
        output.WriteLine($"  Sends: {fast.Sends}");
        output.WriteLine($"  Requests today: {fast.RequestsToday}");
        output.WriteLine();

        var jev = status.Jev;
        output.WriteLine("JEV (legacy)");
        output.WriteLine(jev.State switch
        {
            ConnectionState.Connected => $"  ● OpenRouter API (metered) · {jev.Model} · {jev.Detail}",
            ConnectionState.Error => $"  ✕ OpenRouter API (metered) · {jev.Model} · {jev.Detail}",
            ConnectionState.NotConnected when jev.CanStoreKey => $"  ○ {jev.Detail} — run: Lumen.Engine connections set-openrouter-key",
            _ => $"  ○ {jev.Detail}",
        });
        output.WriteLine($"  Sends: {jev.Sends}");
        output.WriteLine();

        var claude = status.Claude;
        output.WriteLine("Claude");
        output.WriteLine($"  {(claude.State == ConnectionState.Connected ? "●" : "○")} {claude.Detail}{(claude.Version.Length == 0 ? "" : $" · {claude.Version}")}");
        output.WriteLine($"  Investigations: {claude.InvestigationsDetail}");
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
