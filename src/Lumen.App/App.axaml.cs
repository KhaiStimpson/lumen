using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Lumen.App.Motion;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Lumen.App.Views;
using Lumen.Contracts;

namespace Lumen.App;

public sealed class App : Application
{
    /// <summary>Command-line options; see <see cref="LaunchOptions.Parse"/>.</summary>
    public static LaunchOptions Options { get; set; } = new();

    public static IMotionService Motion { get; private set; } = new MotionService(() => false);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        RequestedThemeVariant = (Options.Theme ?? settings.Theme) switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        var reducedMotion = Options.ReducedMotion ?? settings.ReducedMotion ?? SystemMotionPreference.PrefersReducedMotion();
        Motion = new MotionService(() => reducedMotion);

        IReviewSource source = Options.FixtureDirectory is { } fixture
            ? new FixtureReviewSource(fixture)
            : new EngineReviewSource(new EngineLauncher(EngineEndpoint.DefaultName));

        var viewModel = new MainWindowViewModel(source, settings, AppSettings.DefaultPath);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += async (_, _) => await source.DisposeAsync().ConfigureAwait(false);

            if (Options.PullRequest is { } reference)
            {
                _ = viewModel.OpenAsync(reference);
            }

            if (Options.CapturePath is { } capture)
            {
                _ = DevCapture.RunAsync(window, viewModel, capture, Options.CaptureScript);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}

public sealed record LaunchOptions
{
    /// <summary>Open this pull request on start (URL or owner/repo#n).</summary>
    public string? PullRequest { get; init; }

    /// <summary>Replay a recorded fixture instead of talking to the engine.</summary>
    public string? FixtureDirectory { get; init; }

    public string? Theme { get; init; }

    public bool? ReducedMotion { get; init; }

    /// <summary>Development aid: render the window to PNGs under this directory, then exit.</summary>
    public string? CapturePath { get; init; }

    /// <summary>Comma-separated steps for the capture, e.g. "diff,next,examine,evidence".</summary>
    public string? CaptureScript { get; init; }

    public static LaunchOptions Parse(string[] args)
    {
        var options = new LaunchOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            options = args[i] switch
            {
                "--pr" => options with { PullRequest = Next() },
                "--fixture" => options with { FixtureDirectory = Next() },
                "--theme" => options with { Theme = Next() },
                "--reduced-motion" => options with { ReducedMotion = true },
                "--capture" => options with { CapturePath = Next() },
                "--capture-script" => options with { CaptureScript = Next() },
                var other when other.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) => options with { PullRequest = other },
                _ => options,
            };
        }

        return options;
    }
}
