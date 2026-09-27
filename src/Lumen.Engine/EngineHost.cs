using Lumen.Analysis;
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
        services.AddSingleton<IReviewStore>(_ => new SqliteReviewStore(Path.Combine(options.DataDirectory, "lumen.db")));

        services.AddSingleton<IChangeDetector, PeerPatternDetector>();
        services.AddSingleton<IAttentionPolicy, RuleBasedAttentionPolicy>();
        services.AddSingleton<IReviewPointExplainer, PeerDeviationExplainer>();
        services.AddSingleton<ReviewPointPipeline>();
        services.AddSingleton<PullRequestSessionManager>();

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
}
