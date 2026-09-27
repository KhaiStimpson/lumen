using Lumen.Agents;
using Lumen.Agents.ClaudeCode;
using Lumen.Agents.Execution;
using Lumen.Agents.Investigations;
using Lumen.Analysis;
using Lumen.Jev;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Engine.Hosting;
using Lumen.Engine.Services;
using Lumen.Engine.Sessions;
using Lumen.GitHub;
using Lumen.Repository;
using Lumen.Roslyn;
using Lumen.Storage;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Lumen.Engine;

public sealed record EngineOptions
{
    public string PipeName { get; init; } = EngineEndpoint.DefaultName;

    /// <summary>The desktop client's process; the engine exits when it does.</summary>
    public int? ParentProcessId { get; init; }

    public string DataDirectory { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumen");

    public static EngineOptions FromArgs(string[] args)
    {
        var options = new EngineOptions();
        for (var i = 0; i < args.Length - 1; i++)
        {
            options = args[i] switch
            {
                "--pipe" => options with { PipeName = args[i + 1] },
                "--parent-pid" when int.TryParse(args[i + 1], out var pid) => options with { ParentProcessId = pid },
                "--data-dir" => options with { DataDirectory = args[i + 1] },
                _ => options,
            };
        }

        return options;
    }
}

public static class EngineHost
{
    /// <summary>Builds the engine. Tests pass <paramref name="configureServices"/> to swap GitHub, git or storage.</summary>
    public static WebApplication Build(EngineOptions options, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (OperatingSystem.IsWindows())
            {
                kestrel.ListenNamedPipe(options.PipeName, l => l.Protocols = HttpProtocols.Http2);
            }
            else
            {
                var socket = EngineEndpoint.UnixSocketPath(options.PipeName);
                File.Delete(socket);
                kestrel.ListenUnixSocket(socket, l => l.Protocols = HttpProtocols.Http2);
            }
        });

        var services = builder.Services;
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddGrpc(o => o.MaxSendMessageSize = 64 * 1024 * 1024);

        services.AddSingleton<IGitHubTokenSource, GhCliTokenSource>();
        services.AddHttpClient<IGitHubClient, GitHubRestClient>();
        services.AddSingleton<IRepositoryWorkspace>(sp => new GitRepositoryWorkspace(
            RepositoryWorkspaceOptions.FromTokenSource(sp.GetRequiredService<IGitHubTokenSource>()) with
            {
                CacheRoot = options.DataDirectory,
            }));
        services.AddSingleton(_ => new SqliteReviewStore(Path.Combine(options.DataDirectory, "lumen.db")));
        services.AddSingleton<IReviewStore>(sp => sp.GetRequiredService<SqliteReviewStore>());
        services.AddSingleton<IAttentionEvaluationStore>(sp => sp.GetRequiredService<SqliteReviewStore>());
        services.AddSingleton<IInvestigationStore>(sp => sp.GetRequiredService<SqliteReviewStore>());

        services.AddSingleton(_ => EngineSettings.Load(options.DataDirectory));
        services.AddSingleton<ISecretStore>(_ => OperatingSystem.IsWindows() ? new WindowsCredentialStore() : new UnavailableSecretStore());

        services.AddSingleton<IChangeDetector, PeerPatternDetector>();
        services.AddSingleton<RuleBasedAttentionPolicy>();
        AddJev(services);
        services.AddSingleton<IReviewPointExplainer, PeerDeviationExplainer>();
        services.AddSingleton<ReviewPointPipeline>();
        AddAgents(services, options);
        services.AddSingleton<PullRequestSessionManager>();
        services.AddTransient(sp => new ConnectionsProbe(
            sp.GetRequiredService<EngineSettings>(),
            options.DataDirectory,
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<IOpenRouterKeyCheck>(),
            sp.GetRequiredService<IAgentProvider>()));

        if (options.ParentProcessId is { } parent)
        {
            services.AddHostedService(sp => new ParentProcessWatcher(
                parent,
                sp.GetRequiredService<IHostApplicationLifetime>(),
                sp.GetRequiredService<ILogger<ParentProcessWatcher>>()));
        }

        configureServices?.Invoke(services);

        var app = builder.Build();
        app.MapGrpcService<ReviewEngineService>();
        return app;
    }

    /// <summary>JEV on top of the rules (TDD §38). Without a key or permission it is a pass-through to the rules.</summary>
    internal static void AddJev(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var jev = sp.GetRequiredService<EngineSettings>().Jev;
            return new OpenRouterOptions { Model = jev.Model, RequireZeroDataRetention = jev.RequireZeroDataRetention };
        });
        services.AddHttpClient<ISystemOneEvaluator, OpenRouterSystemOneEvaluator>();
        services.AddHttpClient<IOpenRouterKeyCheck, OpenRouterSystemOneEvaluator>();
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<EngineSettings>();
            return new JevAttentionPolicy(
                sp.GetRequiredService<RuleBasedAttentionPolicy>(),
                sp.GetRequiredService<ISystemOneEvaluator>(),
                sp.GetRequiredService<IAttentionEvaluationStore>(),
                () => settings.Privacy,
                new JevPolicyOptions { Enabled = settings.Jev.Enabled, OverallTimeout = TimeSpan.FromSeconds(settings.Jev.TimeoutSeconds) },
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<JevAttentionPolicy>>());
        });
        services.AddSingleton<IAttentionPolicy>(sp => sp.GetRequiredService<JevAttentionPolicy>());
    }

    /// <summary>Investigation agents (TDD §11–13, §35–37), sandboxed to the engine's data directory (§41).</summary>
    internal static void AddAgents(IServiceCollection services, EngineOptions options)
    {
        services.AddSingleton<IProcessRunner>(_ => new SandboxedProcessRunner(new CommandPolicy
        {
            AllowedExecutables = new HashSet<string>(StringComparer.Ordinal) { ClaudeCodeProvider.Executable },
            AllowedWorkingRoots = [options.DataDirectory],
        }));
        services.AddSingleton<IAgentProvider>(sp => new ClaudeCodeProvider(
            sp.GetRequiredService<IProcessRunner>(),
            Path.Combine(options.DataDirectory, "agent-state")));
        services.AddSingleton<IAgentWorktreeFactory>(_ => new GitAgentWorktreeFactory(Path.Combine(options.DataDirectory, "agents")));
        services.AddSingleton<IInvestigator, RepositoryPatternInvestigator>();
        services.AddSingleton(sp =>
        {
            var agents = sp.GetRequiredService<EngineSettings>().Agents;
            return new InvestigationScheduler(
                sp.GetServices<IInvestigator>(),
                sp.GetRequiredService<IAgentProvider>(),
                sp.GetRequiredService<IInvestigationStore>(),
                sp.GetRequiredService<IAgentWorktreeFactory>(),
                new InvestigationSchedulerOptions
                {
                    MaxConcurrent = agents.MaxConcurrent,
                    MaxPerPullRequest = agents.MaxInvestigationsPerPullRequest,
                    AllowMeteredUsage = agents.AllowMeteredUsage,
                },
                sp.GetRequiredService<ILogger<InvestigationScheduler>>());
        });
        services.AddSingleton<InvestigationCoordinator>();
    }
}
