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
    public async Task DraggingSplittersResizesPanesToAnyWidthAndSavesThem()
    {
        await using var session = await Session.OpenAsync();
        var view = session.Window.GetVisualDescendants().OfType<PullRequestView>().Single();
        var columns = view.FindControl<Grid>("Layout")!.ColumnDefinitions;
        var left = columns[0];
        var right = columns[4];
        var leftSplitter = view.FindControl<GridSplitter>("LeftSplitter")!;
        var rightSplitter = view.FindControl<GridSplitter>("RightSplitter")!;
        Assert.Equal(284, left.ActualWidth, 1);
        Assert.Equal(344, right.ActualWidth, 1);

        async Task DragAsync(GridSplitter splitter, double dx)
        {
            var origin = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, 200), session.Window)!.Value;
            session.Window.MouseDown(origin, MouseButton.Left);
            await Fixture.PumpAsync(2);
            session.Window.MouseMove(new Point(origin.X + dx / 2, origin.Y));
            session.Window.MouseMove(new Point(origin.X + dx, origin.Y));
            await Fixture.PumpAsync(2);
            session.Window.MouseUp(new Point(origin.X + dx, origin.Y), MouseButton.Left);
            await Fixture.PumpAsync();
        }

        await DragAsync(leftSplitter, 150);
        Assert.Equal(434, left.ActualWidth, 1);

        await DragAsync(leftSplitter, -400);
        Assert.True(left.ActualWidth < 60, $"left pane should shrink well below the old 200px minimum, was {left.ActualWidth}");

        await DragAsync(rightSplitter, -200);
        Assert.Equal(544, right.ActualWidth, 1);

        var saved = AppSettings.Load(session.SettingsPath);
        Assert.Equal(left.ActualWidth, saved.LeftPaneWidth!.Value, 1);
        Assert.Equal(right.ActualWidth, saved.RightPaneWidth!.Value, 1);
    }

    [AvaloniaFact]
    public async Task ClickingAFilesCheckboxMarksItViewedWithoutOpeningIt()
    {
        Directory.CreateDirectory(ScreensDirectory);
        await using var session = await Session.OpenAsync();
        var pr = session.PullRequest;
        // The file tree sits behind the nav rail's Files item; the review plan is the default list.
        pr.ShowFiles();
        await Fixture.PumpAsync(4);
        Assert.True(Find<RadioButton>(session.Window, "TabFiles").IsChecked);
        var tree = Find<TreeView>(session.Window, "FileTree");
        var box = tree.GetVisualDescendants().OfType<CheckBox>()
            .First(c => c.IsEffectivelyVisible && c.DataContext is FileTreeNode { File: { } f } && f != pr.SelectedFile
                && c.TranslatePoint(default, tree) is { Y: >= 0 } p && p.Y + c.Bounds.Height <= tree.Bounds.Height);
        var file = ((FileTreeNode)box.DataContext!).File!;
        var selected = pr.SelectedFile;

        async Task ClickAsync()
        {
            var centre = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), session.Window)!.Value;
            session.Window.MouseDown(centre, MouseButton.Left);
            session.Window.MouseUp(centre, MouseButton.Left);
            await Fixture.PumpAsync();
        }

        await ClickAsync();
        Assert.True(file.IsViewed);
        Assert.True(box.IsChecked);
        Assert.Same(selected, pr.SelectedFile);
        Assert.Equal("1 / 60 viewed", pr.ViewedSummary);
        Capture(session.Window, "12-viewed-checkbox.png");

        await ClickAsync();
        Assert.False(file.IsViewed);
        Assert.False(box.IsChecked);
    }

    [AvaloniaFact]
    public async Task FullFileSegmentAndXToggleBetweenHunksAndTheWholeFile()
    {
        Directory.CreateDirectory(ScreensDirectory);
        await using var session = await Session.OpenAsync();
        var pr = session.PullRequest;
        var changes = Find<RadioButton>(session.Window, "ShowChanges");
        var full = Find<RadioButton>(session.Window, "ShowFullFile");
        var editor = session.Window.GetVisualDescendants().OfType<Diff.DiffEditor>().First(e => e.Name == "Editor");
        Assert.True(changes.IsChecked);

        // A modified file with several hunks, so there is unchanged code between them to bring back.
        await pr.SelectFileAsync(pr.Files.First(f => f.Path == "src/AndrewCrm.Web/Program.cs"));
        await Fixture.PumpAsync();
        var hunks = editor.Diff!;
        Assert.Contains(hunks.Rows, r => r.Kind == Diff.DiffRowKind.HunkHeader);

        async Task ClickAsync(Control control)
        {
            var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), session.Window)!.Value;
            session.Window.MouseDown(centre, MouseButton.Left);
            session.Window.MouseUp(centre, MouseButton.Left);
            await Fixture.PumpAsync();
        }

        await ClickAsync(full);
        Assert.True(pr.ShowFullFile);
        Assert.False(changes.IsChecked);
        Assert.DoesNotContain(editor.Diff!.Rows, r => r.Kind == Diff.DiffRowKind.HunkHeader);
        Assert.True(editor.Diff.Rows.Count > hunks.Rows.Count);
        Capture(session.Window, "13-full-file.png");

        await ClickAsync(changes);
        Assert.False(pr.ShowFullFile);
        Assert.Equal(hunks.Text, editor.Diff!.Text);

        // X flips it from the keyboard, and the segments follow both ways.
        await session.PressAsync(PhysicalKey.X);
        Assert.True(pr.ShowFullFile);
        Assert.True(full.IsChecked);
        await session.PressAsync(PhysicalKey.X);
        Assert.False(pr.ShowFullFile);
        Assert.True(changes.IsChecked);
        Assert.False(full.IsChecked);
    }

    [AvaloniaFact]
    public async Task SavedPaneWidthsAreRestoredAtStartup()
    {
        await using var session = await Session.OpenAsync(new AppSettings { LeftPaneWidth = 410, RightPaneWidth = 275 });
        var view = session.Window.GetVisualDescendants().OfType<PullRequestView>().Single();
        Assert.Equal(410, view.FindControl<Grid>("Layout")!.ColumnDefinitions[0].ActualWidth, 1);
        Assert.Equal(275, view.FindControl<Grid>("Layout")!.ColumnDefinitions[4].ActualWidth, 1);
    }

    [AvaloniaFact]
    public async Task ShortcutsDoNotFireWhileATextBoxHasFocus()
    {
        await using var session = await Session.OpenAsync();
        var pr = session.PullRequest;
        var file = pr.SelectedFile;

        var goToFile = session.Window.FindControl<TextBox>("GoToFile");
        Assert.NotNull(goToFile);
        Assert.True(goToFile.Focus());
        Assert.Same(goToFile, session.Window.FocusManager?.GetFocusedElement());

        await session.PressAsync(PhysicalKey.Period);
        Assert.Same(file, pr.SelectedFile);
        await session.PressAsync(PhysicalKey.I);
        Assert.True(pr.IsRightPaneOpen);
    }

    [AvaloniaFact]
    public async Task EscapeLeavesTheTextBoxAndShortcutsWorkAgain()
    {
        await using var session = await Session.OpenAsync();
        var pr = session.PullRequest;

        var goToFile = session.Window.FindControl<TextBox>("GoToFile");
        Assert.NotNull(goToFile);
        Assert.True(goToFile.Focus());
        var file = pr.SelectedFile;
        await session.PressAsync(PhysicalKey.Period);
        Assert.Same(file, pr.SelectedFile);

        await session.PressAsync(PhysicalKey.Escape);
        Assert.IsNotType<TextBox>(session.Window.FocusManager?.GetFocusedElement());
        await session.PressAsync(PhysicalKey.Period);
        await Fixture.WaitUntilAsync(() => pr.SelectedFile != file);
    }

    [AvaloniaFact]
    public async Task SettingsAreModalAndEscapeClosesThem()
    {
        await using var session = await Session.OpenAsync();
        var (vm, pr) = (session.ViewModel, session.PullRequest);
        var first = pr.SelectedFile;

        await vm.OpenReviewSettingsCommand.ExecuteAsync(null);
        await session.PressAsync(PhysicalKey.Period);
        Assert.Same(first, pr.SelectedFile);

        await session.PressAsync(PhysicalKey.Escape);
        Assert.False(vm.IsSettingsOpen);
        Assert.False(session.Window.FindControl<Panel>("SettingsOverlay")!.IsEffectivelyVisible);

        await session.PressAsync(PhysicalKey.Period);
        await Fixture.WaitUntilAsync(() => pr.SelectedFile != first);
    }

    [AvaloniaFact]
    public async Task ClickingOutsideTheSettingsCardClosesIt()
    {
        await using var session = await Session.OpenAsync();
        var (window, vm) = (session.Window, session.ViewModel);
        await vm.OpenReviewSettingsCommand.ExecuteAsync(null);
        await Fixture.PumpAsync();

        var card = window.FindControl<Border>("SettingsCard")!;
        var inside = card.TranslatePoint(new Point(40, 40), window)!.Value;
        window.MouseDown(inside, MouseButton.Left);
        window.MouseUp(inside, MouseButton.Left);
        await Fixture.PumpAsync();
        Assert.True(vm.IsSettingsOpen);

        window.MouseDown(new Point(60, 500), MouseButton.Left);
        window.MouseUp(new Point(60, 500), MouseButton.Left);
        await Fixture.PumpAsync();
        Assert.False(vm.IsSettingsOpen);
    }

    [AvaloniaFact]
    public async Task AppearanceChangesThemeAndMotionAtOnce()
    {
        var app = Application.Current!;
        try
        {
            await using var session = await Session.OpenAsync();
            var (window, vm) = (session.Window, session.ViewModel);
            await vm.OpenReviewSettingsCommand.ExecuteAsync(null);
            vm.ShowSectionCommand.Execute(SettingsSection.Appearance);
            await Fixture.PumpAsync();

            Find<RadioButton>(window, "ThemeDark").IsChecked = true;
            Find<RadioButton>(window, "MotionReduced").IsChecked = true;
            await Fixture.PumpAsync(20);

            Assert.Equal(ThemeVariant.Dark, app.ActualThemeVariant);
            Assert.True(App.ReducedMotionPreference);
            Capture(window, "11-settings-appearance-dark.png");

            // The title bar button and the setting stay in step.
            vm.ToggleThemeCommand.Execute(null);
            Assert.Equal(ThemeChoice.Light, vm.Appearance.Theme);
            Assert.True(Find<RadioButton>(window, "ThemeLight").IsChecked);
        }
        finally
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
            App.ReducedMotionPreference = null;
        }
    }

    private static T Find<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Capture(Window window, string name)
    {
        Directory.CreateDirectory(ScreensDirectory);
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

        public string SettingsPath => _settingsPath;

        private Session(MainWindow window, MainWindowViewModel viewModel, PullRequestViewModel pullRequest, FixtureReviewSource source, string settingsPath)
        {
            Source = source;
            Window = window;
            ViewModel = viewModel;
            PullRequest = pullRequest;
            _settingsPath = settingsPath;
        }

        public FixtureReviewSource Source { get; }

        public MainWindow Window { get; }

        public MainWindowViewModel ViewModel { get; }

        public PullRequestViewModel PullRequest { get; }

        public static async Task<Session> OpenAsync(AppSettings? settings = null)
        {
            var settingsPath = Path.Combine(Path.GetTempPath(), "lumen-app-tests", $"{Guid.NewGuid():N}.json");
            var source = Fixture.CreateSource();
            var viewModel = new MainWindowViewModel(source, settings ?? new AppSettings(), settingsPath);
            var window = new MainWindow { DataContext = viewModel };
            window.Show();

            await viewModel.OpenAsync(Fixture.Reference);
            var pr = viewModel.PullRequest;
            Assert.NotNull(pr);
            await Fixture.WaitUntilAsync(() => !pr.IsAnalysing);
            Assert.Null(pr.Error);
            await Fixture.PumpAsync(10);
            return new Session(window, viewModel, pr, source, settingsPath);
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
