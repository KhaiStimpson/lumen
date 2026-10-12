using Lumen.Agents;
using Lumen.Agents.ClaudeCode;
using Lumen.Agents.Execution;
using Lumen.Agents.Investigations;
using Lumen.Analysis;
using Lumen.Jev;
using Lumen.Models;
using Lumen.Models.Cloudflare;
using Lumen.Engine.Walkthroughs;
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

    /// <summary>How often an open pull request's conversation, checks and threads are re-read while watched.</summary>
    public TimeSpan FollowInterval { get; init; } = TimeSpan.FromSeconds(90);

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
        services.AddSingleton<IPullRequestVisitStore>(sp => sp.GetRequiredService<SqliteReviewStore>());
        services.AddSingleton<IWalkthroughStore>(sp => sp.GetRequiredService<SqliteReviewStore>());

        services.AddSingleton(_ => EngineSettings.Load(options.DataDirectory));
        services.AddSingleton(sp => new EngineSettingsStore(sp.GetRequiredService<EngineSettings>(), options.DataDirectory));
        services.AddSingleton(sp => new ReviewSettingsStore(sp.GetRequiredService<EngineSettingsStore>(), options.DataDirectory));
        services.AddSingleton<ISecretStore>(_ => OperatingSystem.IsWindows() ? new WindowsCredentialStore() : new UnavailableSecretStore());

        services.AddSingleton<IChangeDetector, PeerPatternDetector>();
        services.AddSingleton<RuleBasedAttentionPolicy>();
        AddJev(services);
        services.AddSingleton<IReviewPointExplainer, PeerDeviationExplainer>();
        services.AddSingleton<ReviewPointPipeline>();
        AddAgents(services, options);
        AddFastModel(services, options);
        services.AddSingleton<PullRequestSessionManager>();
        services.AddSingleton<ReviewActions>();
        services.AddSingleton<Inbox.InboxService>();
        services.AddTransient(sp => new ConnectionsProbe(
            sp.GetRequiredService<EngineSettingsStore>(),
            options.DataDirectory,
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<IOpenRouterKeyCheck>(),
            sp.GetRequiredService<IAgentProvider>(),
            sp.GetRequiredService<IModelProvider>(),
            sp.GetRequiredService<IModelUsageMeter>()));

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

        // Created eagerly so it hears every session that becomes ready.
        _ = app.Services.GetRequiredService<WalkthroughCoordinator>();
        return app;
    }

    /// <summary>JEV on top of the rules (TDD §38). Without a key or permission it is a pass-through to the rules.</summary>
    internal static void AddJev(IServiceCollection services)
    {
        // Read at each request, so a model or retention change in Settings applies without a restart.
        static OpenRouterSystemOneEvaluator Create(HttpClient http, IServiceProvider sp)
        {
            var settings = sp.GetRequiredService<EngineSettingsStore>();
            return new OpenRouterSystemOneEvaluator(
                http,
                sp.GetRequiredService<ISecretStore>(),
                () => new OpenRouterOptions { Model = settings.Current.Jev.Model, RequireZeroDataRetention = settings.Current.Jev.RequireZeroDataRetention });
        }

        services.AddHttpClient<ISystemOneEvaluator, OpenRouterSystemOneEvaluator>(Create);
        services.AddHttpClient<IOpenRouterKeyCheck, OpenRouterSystemOneEvaluator>(Create);
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<EngineSettingsStore>();
            return new JevAttentionPolicy(
                sp.GetRequiredService<RuleBasedAttentionPolicy>(),
                sp.GetRequiredService<ISystemOneEvaluator>(),
                sp.GetRequiredService<IAttentionEvaluationStore>(),
                () => settings.Current.Privacy,
                () => new JevPolicyOptions
                {
                    Enabled = settings.Current.Jev.Enabled,
                    OverallTimeout = TimeSpan.FromSeconds(settings.Current.Jev.TimeoutSeconds),
                },
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<JevAttentionPolicy>>());
        });
        // Plan v2: JEV is legacy and unregistered unless switched on at start; the rules alone decide otherwise.
        services.AddSingleton<IAttentionPolicy>(sp => sp.GetRequiredService<EngineSettingsStore>().Current.Jev.Enabled
            ? sp.GetRequiredService<JevAttentionPolicy>()
            : sp.GetRequiredService<RuleBasedAttentionPolicy>());
    }

    /// <summary>The fast model (plan v2 M7) and the walkthrough built on it (M8). Read live, like JEV's settings.</summary>
    internal static void AddFastModel(IServiceCollection services, EngineOptions options)
    {
        services.AddSingleton<IModelUsageMeter>(sp =>
        {
            var settings = sp.GetRequiredService<EngineSettingsStore>();
            return new DailyUsageMeter(options.DataDirectory, () => settings.Current.FastModel.DailyRequestCap, sp.GetRequiredService<TimeProvider>());
        });
        services.AddHttpClient<IModelProvider, CloudflareWorkersAiProvider>((http, sp) =>
        {
            var settings = sp.GetRequiredService<EngineSettingsStore>();
            return new CloudflareWorkersAiProvider(
                http,
                sp.GetRequiredService<ISecretStore>(),
                () => new CloudflareOptions
                {
                    Model = settings.Current.FastModel.Model,
                    Gateway = settings.Current.FastModel.Gateway,
                    Timeout = TimeSpan.FromSeconds(settings.Current.FastModel.TimeoutSeconds),
                },
                sp.GetRequiredService<IModelUsageMeter>());
        });
        services.AddSingleton<WalkthroughCoordinator>();
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
            var settings = sp.GetRequiredService<EngineSettingsStore>();
            return new InvestigationScheduler(
                sp.GetServices<IInvestigator>(),
                sp.GetRequiredService<IAgentProvider>(),
                sp.GetRequiredService<IInvestigationStore>(),
                sp.GetRequiredService<IAgentWorktreeFactory>(),
                () => new InvestigationSchedulerOptions
                {
                    MaxConcurrent = settings.Current.Agents.MaxConcurrent,
                    MaxPerPullRequest = settings.Current.Agents.MaxInvestigationsPerPullRequest,
                    AllowMeteredUsage = settings.Current.Agents.AllowMeteredUsage,
                },
                sp.GetRequiredService<ILogger<InvestigationScheduler>>());
        });
        services.AddSingleton<InvestigationCoordinator>();
    }
}
