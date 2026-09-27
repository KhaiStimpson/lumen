using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Lumen.App.Views;
using Lumen.Contracts;

namespace Lumen.App.Tests;

/// <summary>The real window, styles and editor on the headless platform, driven through the keyboard.</summary>
public sealed class MainWindowTests
{
    private static string ScreensDirectory => Path.Combine(AppContext.BaseDirectory, "screens");

    [AvaloniaFact]
    public async Task JAndKStepThroughReviewPoints()
    {
        await using var session = await Session.OpenAsync();
        var (window, pr) = (session.Window, session.PullRequest);
        var points = pr.ReviewPoints.ToList();
        Assert.Same(points[0], pr.CurrentPoint);

        await session.PressAsync(PhysicalKey.J);
        Assert.Same(points[1], pr.CurrentPoint);

        await session.PressAsync(PhysicalKey.J);
        Assert.Same(points[2], pr.CurrentPoint);

        await session.PressAsync(PhysicalKey.K);
        Assert.Same(points[1], pr.CurrentPoint);

        await session.PressAsync(PhysicalKey.K);
        Assert.Same(points[0], pr.CurrentPoint);
        Assert.Equal(Fixture.TopPointPath, pr.SelectedFile?.Path);
        Assert.NotNull(window);
    }

    [AvaloniaFact]
    public async Task EnterExaminesAndEscapeReturnsToDiff()
    {
        await using var session = await Session.OpenAsync();
        var pr = session.PullRequest;

        await session.PressAsync(PhysicalKey.Enter);
        Assert.Equal(ReviewMode.Examine, pr.Mode);
        Assert.True(session.Window.GetVisualDescendants().OfType<ExamineView>().Single().IsEffectivelyVisible);

        await session.PressAsync(PhysicalKey.Escape);
        Assert.Equal(ReviewMode.Diff, pr.Mode);
        Assert.False(session.Window.GetVisualDescendants().OfType<ExamineView>().Single().IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task DDismissesTheCurrentPoint()
    {
        await using var session = await Session.OpenAsync();
        var pr = session.PullRequest;
        var first = pr.CurrentPoint!;

        await session.PressAsync(PhysicalKey.D);

        Assert.Equal(ReviewPointState.Dismissed, first.State);
        Assert.NotSame(first, pr.CurrentPoint);
        Assert.Same(pr.ReviewPoints[1], pr.CurrentPoint);
        Assert.Equal("1 of 2", pr.PositionLabel);
    }

    [AvaloniaFact]
    public async Task CStartsComposingInTheInlineCard()
    {
        await using var session = await Session.OpenAsync();
        var pr = session.PullRequest;
        var point = pr.CurrentPoint!;

        await session.PressAsync(PhysicalKey.C);
        Assert.True(point.IsComposing);
        Assert.Equal(point.SuggestedComment, point.CommentDraft);
        Assert.Equal(ReviewMode.Diff, pr.Mode);

        var composer = session.Window.GetVisualDescendants().OfType<TextBox>()
            .SingleOrDefault(t => t.Classes.Contains("composer") && t.IsEffectivelyVisible);
        Assert.NotNull(composer);
        Assert.Equal(point.SuggestedComment, composer.Text);
    }

    [AvaloniaFact]
    public async Task ShortcutsDoNotFireWhileATextBoxHasFocus()
    {
        await using var session = await Session.OpenAsync();
        var pr = session.PullRequest;
        var point = pr.CurrentPoint!;

        var goToFile = session.Window.FindControl<TextBox>("GoToFile");
        Assert.NotNull(goToFile);
        Assert.True(goToFile.Focus());
        Assert.Same(goToFile, session.Window.FocusManager?.GetFocusedElement());

        await session.PressAsync(PhysicalKey.J);
        Assert.Same(point, pr.CurrentPoint);
        await session.PressAsync(PhysicalKey.D);
        Assert.False(point.IsDismissed);
        await session.PressAsync(PhysicalKey.Enter);
        Assert.Equal(ReviewMode.Diff, pr.Mode);
    }

    [AvaloniaFact]
    public async Task EscapeLeavesTheTextBoxAndShortcutsWorkAgain()
    {
        await using var session = await Session.OpenAsync();
        var pr = session.PullRequest;

        var goToFile = session.Window.FindControl<TextBox>("GoToFile");
        Assert.NotNull(goToFile);
        Assert.True(goToFile.Focus());
        await session.PressAsync(PhysicalKey.J);
        Assert.Same(pr.ReviewPoints[0], pr.CurrentPoint);

        await session.PressAsync(PhysicalKey.Escape);
        Assert.IsNotType<TextBox>(session.Window.FocusManager?.GetFocusedElement());
        await session.PressAsync(PhysicalKey.J);
        Assert.Same(pr.ReviewPoints[1], pr.CurrentPoint);
    }

    [AvaloniaFact]
    public async Task CInExamineFocusesTheComposerAndTypingDoesNotNavigate()
    {
        await using var session = await Session.OpenAsync();
        var pr = session.PullRequest;
        var point = pr.CurrentPoint!;

        await session.PressAsync(PhysicalKey.Enter);
        Assert.Equal(ReviewMode.Examine, pr.Mode);

        await session.PressAsync(PhysicalKey.C);
        Assert.True(point.IsComposing);
        Assert.Equal("comment", pr.ExamineTab);

        var box = Assert.IsType<TextBox>(session.Window.FocusManager?.GetFocusedElement());
        Assert.Contains("composer", box.Classes);

        await session.PressAsync(PhysicalKey.J);
        Assert.Same(point, pr.CurrentPoint);
        await session.PressAsync(PhysicalKey.D);
        Assert.False(point.IsDismissed);
        Assert.True(point.IsComposing);
        Assert.Equal(ReviewMode.Examine, pr.Mode);

        await session.PressAsync(PhysicalKey.Escape);
        Assert.False(point.IsComposing);
    }

    [AvaloniaFact]
    public async Task CFocusesTheInlineComposerAndTypingDoesNotNavigate()
    {
        await using var session = await Session.OpenAsync();
        var pr = session.PullRequest;
        var point = pr.CurrentPoint!;

        await session.PressAsync(PhysicalKey.C);
        Assert.True(point.IsComposing);

        var focused = session.Window.FocusManager?.GetFocusedElement();
        var box = Assert.IsType<TextBox>(focused);
        Assert.Contains("composer", box.Classes);

        // While typing, review shortcuts must not fire.
        await session.PressAsync(PhysicalKey.J);
        Assert.Same(point, pr.CurrentPoint);
        await session.PressAsync(PhysicalKey.D);
        Assert.False(point.IsDismissed);
        Assert.True(point.IsComposing);

        // Escape leaves the composer; shortcuts work again.
        await session.PressAsync(PhysicalKey.Escape);
        Assert.False(point.IsComposing);
        await session.PressAsync(PhysicalKey.J);
        Assert.Same(pr.ReviewPoints[1], pr.CurrentPoint);
    }

    [AvaloniaFact]
    public async Task RendersDiffWithInlineCardExamineAndDarkTheme()
    {
        Directory.CreateDirectory(ScreensDirectory);
        await using var session = await Session.OpenAsync();
        var (window, pr) = (session.Window, session.PullRequest);

        // Let the arrival motion settle before looking.
        await Fixture.PumpAsync(40);

        var card = window.GetVisualDescendants().OfType<ReviewCardView>()
            .FirstOrDefault(c => c.DataContext is CardContext { IsPrimary: true } context && context.Point == pr.CurrentPoint);
        Assert.NotNull(card);
        Assert.True(card.IsEffectivelyVisible);
        Assert.True(card.Bounds.Height > 0);

        Capture(window, "01-diff.png");

        await pr.ExamineAsync(null);
        await Fixture.PumpAsync(40);
        Capture(window, "02-examine.png");

        await pr.ShowEvidenceAsync(null);
        await Fixture.PumpAsync(40);
        Capture(window, "03-examine-evidence.png");

        pr.CloseExamine();
        var app = Application.Current!;
        app.RequestedThemeVariant = ThemeVariant.Dark;
        try
        {
            await Fixture.PumpAsync(40);
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            Capture(window, "04-diff-dark.png");
        }
        finally
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
        }
    }

    private static void Capture(Window window, string name)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame.PixelSize.Width > 0 && frame.PixelSize.Height > 0);
        frame.Save(Path.Combine(ScreensDirectory, name), new PngBitmapEncoderOptions());
    }

    /// <summary>A shown main window with andrew-crm#58 open and analysed.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly string _settingsPath;

        private Session(MainWindow window, MainWindowViewModel viewModel, PullRequestViewModel pullRequest, string settingsPath)
        {
            Window = window;
            ViewModel = viewModel;
            PullRequest = pullRequest;
            _settingsPath = settingsPath;
        }

        public MainWindow Window { get; }

        public MainWindowViewModel ViewModel { get; }

        public PullRequestViewModel PullRequest { get; }

        public static async Task<Session> OpenAsync()
        {
            var settingsPath = Path.Combine(Path.GetTempPath(), "lumen-app-tests", $"{Guid.NewGuid():N}.json");
            var viewModel = new MainWindowViewModel(Fixture.CreateSource(), new AppSettings(), settingsPath);
            var window = new MainWindow { DataContext = viewModel };
            window.Show();

            await viewModel.OpenAsync(Fixture.Reference);
            var pr = viewModel.PullRequest;
            Assert.NotNull(pr);
            await Fixture.WaitUntilAsync(() => !pr.IsAnalysing);
            Assert.Null(pr.Error);
            await Fixture.PumpAsync(10);
            return new Session(window, viewModel, pr, settingsPath);
        }

        public async Task PressAsync(PhysicalKey key)
        {
            Window.KeyPressQwerty(key, RawInputModifiers.None);
            Window.KeyReleaseQwerty(key, RawInputModifiers.None);
            await Fixture.PumpAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await PullRequest.DisposeAsync();
            try
            {
                File.Delete(_settingsPath);
            }
            catch (IOException)
            {
            }
        }
    }
}
