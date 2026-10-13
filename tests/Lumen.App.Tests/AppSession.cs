using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Lumen.App.Views;

namespace Lumen.App.Tests;

/// <summary>The real window with andrew-crm#58 open and analysed, for the v2 review client's tests.</summary>
internal sealed class AppSession : IAsyncDisposable
{
    private AppSession(MainWindow window, MainWindowViewModel viewModel, PullRequestViewModel pullRequest, FixtureReviewSource source, string settingsPath)
    {
        Window = window;
        ViewModel = viewModel;
        PullRequest = pullRequest;
        Source = source;
        SettingsPath = settingsPath;
    }

    public static string ScreensDirectory => Path.Combine(AppContext.BaseDirectory, "screens");

    public MainWindow Window { get; }

    public MainWindowViewModel ViewModel { get; }

    public PullRequestViewModel PullRequest { get; }

    public FixtureReviewSource Source { get; }

    public string SettingsPath { get; }

    /// <param name="viewer">Open as someone other than the author (the fixture's viewer is the author).</param>
    public static async Task<AppSession> OpenAsync(string? viewer = null, AppSettings? settings = null, string? fixtureDirectory = null)
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "lumen-app-tests", $"{Guid.NewGuid():N}.json");
        var source = new FixtureReviewSource(fixtureDirectory ?? Fixture.DirectoryPath, viewerLogin: viewer);
        var viewModel = new MainWindowViewModel(source, settings ?? new AppSettings(), settingsPath);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        await viewModel.OpenAsync(Fixture.Reference);
        var pr = viewModel.PullRequest!;
        await Fixture.WaitUntilAsync(() => !pr.IsAnalysing && pr.HasDetails && pr.Walkthrough is not null);
        Assert.Null(pr.Error);
        await Fixture.PumpAsync(10);
        return new AppSession(window, viewModel, pr, source, settingsPath);
    }

    public T Find<T>(string name)
        where T : Control =>
        Window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    public async Task PressAsync(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Window.KeyPressQwerty(key, modifiers);
        Window.KeyReleaseQwerty(key, modifiers);
        await Fixture.PumpAsync();
    }

    public async Task ClickAsync(Control control)
    {
        var centre = control.TranslatePoint(new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)!.Value;
        Window.MouseDown(centre, MouseButton.Left);
        Window.MouseUp(centre, MouseButton.Left);
        await Fixture.PumpAsync();
    }

    public void Capture(string name)
    {
        Directory.CreateDirectory(ScreensDirectory);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(ScreensDirectory, name), new PngBitmapEncoderOptions());
    }

    public async ValueTask DisposeAsync()
    {
        Window.Close();
        await PullRequest.DisposeAsync();
        try
        {
            File.Delete(SettingsPath);
        }
        catch (IOException)
        {
        }
    }
}
