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
    public async Task SavedPaneWidthsAreRestoredAtStartup()
    {
        await using var session = await Session.OpenAsync(new AppSettings { LeftPaneWidth = 410, RightPaneWidth = 275 });
        var view = session.Window.GetVisualDescendants().OfType<PullRequestView>().Single();
        Assert.Equal(410, view.FindControl<Grid>("Layout")!.ColumnDefinitions[0].ActualWidth, 1);
        Assert.Equal(275, view.FindControl<Grid>("Layout")!.ColumnDefinitions[4].ActualWidth, 1);
    }

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

    [AvaloniaFact]
    public async Task ConnectionsPanelStoresAndRemovesTheOpenRouterKey()
    {
        await using var session = await Session.OpenAsync();
        var (window, connections) = (session.Window, session.ViewModel.Connections);

        window.FindControl<Button>("ConnectionsButton")!.Command!.Execute(null);
        await Fixture.WaitUntilAsync(() => connections.IsLoaded);
        await Fixture.PumpAsync();
        Assert.True(window.FindControl<Panel>("SettingsOverlay")!.IsEffectivelyVisible);
        Assert.Equal("No OpenRouter key", connections.JevStatus);
        Assert.False(connections.SaveKeyCommand.CanExecute(null));

        var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "OpenRouterKey");
        box.Focus();
        window.KeyTextInput("sk-or-v1-fixture");
        await session.PressAsync(PhysicalKey.Enter);
        await Fixture.WaitUntilAsync(() => connections.IsKeyStored);

        Assert.True(session.Source.OpenRouterKeyStored);
        Assert.Equal("", connections.KeyInput);
        Assert.Equal("", box.Text);
        Assert.StartsWith("OpenRouter API · Key valid", connections.JevStatus, StringComparison.Ordinal);
        await Fixture.PumpAsync(20);
        Capture(window, "05-connections.png");

        await connections.RemoveKeyCommand.ExecuteAsync(null);
        Assert.False(session.Source.OpenRouterKeyStored);
        Assert.False(connections.IsKeyStored);
    }

    [AvaloniaFact]
    public async Task ConnectionsSwitchesSaveThroughTheEngine()
    {
        await using var session = await Session.OpenAsync();
        var (window, connections) = (session.Window, session.ViewModel.Connections);
        await session.ViewModel.OpenConnectionsCommand.ExecuteAsync(null);
        await Fixture.PumpAsync();

        ToggleSwitch Switch(string name) => window.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == name);
        Assert.True(Switch("JevEnabled").IsChecked);
        Assert.False(Switch("InvestigationsEnabled").IsChecked);

        Switch("InvestigationsEnabled").IsChecked = true;
        await Fixture.WaitUntilAsync(() => connections.Status!.Claude.InvestigationsEnabled);
        Assert.True(session.Source.ConnectionSettings.InvestigationsEnabled);
        Assert.StartsWith("On", connections.InvestigationsStatus, StringComparison.Ordinal);

        Switch("JevEnabled").IsChecked = false;
        await Fixture.WaitUntilAsync(() => connections.JevState == ConnectionState.Off);
        Assert.False(session.Source.ConnectionSettings.JevEnabled);
        Assert.False(Switch("AllowCodeSnippetsToJev").IsEffectivelyEnabled);
        await Fixture.PumpAsync(20);
        Capture(window, "06-connections-settings.png");

        // Cloud AI is the master switch: the per-service switches keep their values but can't be changed.
        Switch("AllowCloudReasoning").IsChecked = false;
        await Fixture.WaitUntilAsync(() => !session.Source.ConnectionSettings.AllowCloudReasoning);
        Assert.Equal("Off: cloud AI is turned off", connections.JevStatus);
        Assert.False(Switch("InvestigationsEnabled").IsEffectivelyEnabled);
        Assert.True(session.Source.ConnectionSettings.InvestigationsEnabled);
    }

    [AvaloniaFact]
    public async Task SettingsAreModalAndEscapeClosesThem()
    {
        await using var session = await Session.OpenAsync();
        var (vm, pr) = (session.ViewModel, session.PullRequest);
        var first = pr.CurrentPoint;

        await vm.OpenConnectionsCommand.ExecuteAsync(null);
        await session.PressAsync(PhysicalKey.J);
        Assert.Same(first, pr.CurrentPoint);

        await session.PressAsync(PhysicalKey.Escape);
        Assert.False(vm.IsSettingsOpen);
        Assert.False(session.Window.FindControl<Panel>("SettingsOverlay")!.IsEffectivelyVisible);

        await session.PressAsync(PhysicalKey.J);
        Assert.NotSame(first, pr.CurrentPoint);
    }

    [AvaloniaFact]
    public async Task ClickingOutsideTheSettingsCardClosesIt()
    {
        await using var session = await Session.OpenAsync();
        var (window, vm) = (session.Window, session.ViewModel);
        await vm.OpenConnectionsCommand.ExecuteAsync(null);
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
    public async Task PickingAPresetOverridesTheRepositoryAndOffersReanalysis()
    {
        await using var session = await Session.OpenAsync();
        var (window, vm, review, pr) = (session.Window, session.ViewModel, session.ViewModel.Review, session.PullRequest);

        window.FindControl<Button>("SettingsButton")!.Command!.Execute(null);
        await Fixture.WaitUntilAsync(() => review.IsLoaded);
        await Fixture.PumpAsync();
        Assert.Equal(SettingsSection.Review, vm.Section);
        Assert.Equal(SettingsScope.Repository, review.Scope);
        Assert.Equal(ReviewPreset.Balanced, review.Preset);
        Assert.False(Find<StackPanel>(window, "OverrideNote").IsVisible);
        Assert.False(Find<Border>(window, "ReanalyseBar").IsVisible);

        Find<RadioButton>(window, "PresetQuiet").IsChecked = true;
        await Fixture.WaitUntilAsync(() => review.IsOverriding);

        var own = session.Source.RepositoryReviewRules["KhaiStimpson/andrew-crm"];
        Assert.Equal(5, own.Sensitivity.MinimumPeers);
        Assert.Equal(3, session.Source.GlobalReviewRules.Sensitivity.MinimumPeers);
        Assert.Equal("Overrides All repositories (Balanced)", review.OverrideNote);
        Assert.True(pr.NeedsReanalysis);
        await Fixture.PumpAsync(20);
        Assert.True(Find<Border>(window, "ReanalyseBar").IsEffectivelyVisible);
        Capture(window, "07-settings-review.png");

        // Any number that no preset uses makes it Custom, and Advanced says so.
        Find<Button>(window, "AdvancedToggle").Command!.Execute(null);
        await Fixture.PumpAsync();
        Find<NumericUpDown>(window, "MinimumPeers").Value = 6;
        await Fixture.WaitUntilAsync(() => session.Source.RepositoryReviewRules["KhaiStimpson/andrew-crm"].Sensitivity.MinimumPeers == 6);
        Assert.Equal(ReviewPreset.Custom, review.Preset);
        await Fixture.PumpAsync(20);
        Capture(window, "08-settings-advanced.png");

        Find<Button>(window, "ResetToGlobal").Command!.Execute(null);
        await Fixture.WaitUntilAsync(() => !review.IsOverriding);
        Assert.Null(session.Source.RepositoryReviewRules["KhaiStimpson/andrew-crm"].Sensitivity);
        Assert.Equal(ReviewPreset.Balanced, review.Preset);

        // Re-analyse closes Settings and re-runs; nothing ran before it was asked for.
        Find<Button>(window, "ReanalyseButton").Command!.Execute(null);
        await Fixture.WaitUntilAsync(() => !vm.IsSettingsOpen && !pr.IsAnalysing);
        Assert.False(pr.NeedsReanalysis);
        await Fixture.PumpAsync();
        Assert.False(Find<Border>(window, "ReanalyseBanner").IsVisible);
    }

    [AvaloniaFact]
    public async Task TheAllRepositoriesScopeEditsTheGlobalDefaults()
    {
        await using var session = await Session.OpenAsync();
        var (window, vm, review) = (session.Window, session.ViewModel, session.ViewModel.Review);
        await vm.OpenReviewSettingsCommand.ExecuteAsync(null);
        await Fixture.PumpAsync();

        Find<RadioButton>(window, "ReviewScopeGlobal").IsChecked = true;
        await Fixture.PumpAsync();
        Assert.Equal(SettingsScope.AllRepositories, review.Scope);
        Assert.Equal("Every repository, unless one overrides it.", review.ScopeCaption);

        Find<RadioButton>(window, "PresetThorough").IsChecked = true;
        await Fixture.WaitUntilAsync(() => session.Source.GlobalReviewRules.Sensitivity.MinimumPeers == 3
            && Math.Abs(session.Source.GlobalReviewRules.Sensitivity.MinimumSupport - 0.65) < 0.001);
        Assert.Empty(session.Source.RepositoryReviewRules);
        Assert.False(review.IsOverriding);

        // Back on the repository, which still inherits: it shows the new global preset.
        Find<RadioButton>(window, "ReviewScopeRepository").IsChecked = true;
        await Fixture.PumpAsync();
        Assert.Equal(ReviewPreset.Thorough, review.Preset);
        Assert.False(review.IsOverriding);
    }

    [AvaloniaFact]
    public async Task IgnoredNamesAreAddedPerRepositoryOnTopOfTheGlobalOnes()
    {
        await using var session = await Session.OpenAsync();
        var (window, vm, review) = (session.Window, session.ViewModel, session.ViewModel.Review);
        await vm.OpenReviewSettingsCommand.ExecuteAsync(null);
        vm.ShowSectionCommand.Execute(SettingsSection.Files);
        await Fixture.PumpAsync();

        // A global entry first...
        Find<RadioButton>(window, "FilesScopeGlobal").IsChecked = true;
        review.NewIgnoredName = "IClock";
        await review.AddIgnoredNameCommand.ExecuteAsync(null);
        Assert.Equal(["IClock"], session.Source.GlobalReviewRules.IgnoredNames);

        // ...then one for this repository, typed and entered.
        Find<RadioButton>(window, "FilesScopeRepository").IsChecked = true;
        await Fixture.PumpAsync();
        Assert.Equal(["IClock"], review.InheritedIgnoredNames);
        var box = Find<TextBox>(window, "NewIgnoredName");
        box.Focus();
        window.KeyTextInput("  AppDbContext ");
        await session.PressAsync(PhysicalKey.Enter);
        await Fixture.WaitUntilAsync(() => session.Source.RepositoryReviewRules.ContainsKey("KhaiStimpson/andrew-crm"));

        Assert.Equal(["AppDbContext"], session.Source.RepositoryReviewRules["KhaiStimpson/andrew-crm"].IgnoredNames);
        Assert.Equal("", box.Text);
        Assert.True(session.PullRequest.NeedsReanalysis);
        Find<TextBox>(window, "NewMechanicalPath").Text = "src/Generated/**";
        await review.AddMechanicalPathCommand.ExecuteAsync(null);
        await Fixture.PumpAsync(20);
        Capture(window, "09-settings-files.png");

        await review.RemoveIgnoredNameCommand.ExecuteAsync("AppDbContext");
        Assert.Empty(session.Source.RepositoryReviewRules["KhaiStimpson/andrew-crm"].IgnoredNames);
        Assert.Equal(["src/Generated/**"], session.Source.RepositoryReviewRules["KhaiStimpson/andrew-crm"].MechanicalPaths);
    }

    [AvaloniaFact]
    public async Task CloudAiLimitsSaveThroughTheEngine()
    {
        await using var session = await Session.OpenAsync();
        var (window, vm, connections) = (session.Window, session.ViewModel, session.ViewModel.Connections);
        await vm.OpenConnectionsCommand.ExecuteAsync(null);
        vm.ShowSectionCommand.Execute(SettingsSection.CloudLimits);
        await Fixture.PumpAsync();
        Assert.Equal(3, Find<NumericUpDown>(window, "MaxInvestigationsPerPullRequest").Value);

        Find<NumericUpDown>(window, "MaxInvestigationsPerPullRequest").Value = 5;
        await Fixture.WaitUntilAsync(() => session.Source.ConnectionSettings.MaxInvestigationsPerPullRequest == 5);
        Find<ToggleSwitch>(window, "AllowMeteredUsage").IsChecked = true;
        await Fixture.WaitUntilAsync(() => session.Source.ConnectionSettings.AllowMeteredUsage);
        connections.JevModelInput = "~typesafe/jev-latest";
        await Fixture.WaitUntilAsync(() => session.Source.ConnectionSettings.JevModel == "~typesafe/jev-latest");

        // A blank model is never sent; the box goes back to the saved one.
        connections.JevModelInput = " ";
        await Fixture.PumpAsync();
        Assert.Equal("~typesafe/jev-latest", session.Source.ConnectionSettings.JevModel);
        Assert.Equal("~typesafe/jev-latest", connections.JevModelInput);
        Assert.False(session.PullRequest.NeedsReanalysis);
        await Fixture.PumpAsync(20);
        Capture(window, "10-settings-cloud-limits.png");
    }

    [AvaloniaFact]
    public async Task AppearanceChangesThemeAndMotionAtOnce()
    {
        var app = Application.Current!;
        try
        {
            await using var session = await Session.OpenAsync();
            var (window, vm) = (session.Window, session.ViewModel);
            await vm.OpenConnectionsCommand.ExecuteAsync(null);
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
