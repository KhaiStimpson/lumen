using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Lumen.App.Tests.TestAppBuilder))]

namespace Lumen.App.Tests;

/// <summary>
/// Builds the real <see cref="App"/> (theme, tokens, styles) on the headless platform with Skia so frames can be captured.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // Read by App.OnFrameworkInitializationCompleted: never start the real engine, and keep theme/motion deterministic.
        App.Options = new LaunchOptions
        {
            FixtureDirectory = Fixture.DirectoryPath,
            Theme = "Light",
            ReducedMotion = false,
        };

        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
    }
}
