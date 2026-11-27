using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Lumen.App.Views;

namespace Lumen.App.Tests;

/// <summary>
/// The window chrome on Windows (Avalonia 12 draws it in the extended client area): our caption buttons only, no second
/// title, and a title bar that is the system drag area with its controls opted out. The headless platform draws no
/// decorations, so these check the theme and the roles rather than a real window.
/// </summary>
public sealed class WindowChromeTests
{
    [AvaloniaFact]
    public void TheDecorationsThemeBuildsOnlyMinimiseMaximiseAndClose()
    {
        var theme = Assert.IsType<ControlTheme>(Application.Current!.FindResource("KelpieWindowDecorations"));
        var template = Assert.IsAssignableFrom<IWindowDrawnDecorationsTemplate>(
            theme.Setters.OfType<Setter>().Single(s => s.Property == WindowDrawnDecorations.TemplateProperty).Value);
        var content = template.Build().Result;

        var overlay = Assert.IsAssignableFrom<Control>(content.Overlay);
        var buttons = overlay.GetLogicalDescendants().OfType<Button>().ToList();
        Assert.Equal(["PART_MinimizeButton", "PART_MaximizeButton", "PART_CloseButton"], buttons.Select(b => b.Name));
        Assert.Equal(
            [WindowDecorationsElementRole.MinimizeButton, WindowDecorationsElementRole.MaximizeButton, WindowDecorationsElementRole.CloseButton],
            buttons.Select(WindowDecorationProperties.GetElementRole));

        // A non-focusable caption button never raises Click on Windows.
        var captionTheme = Assert.IsType<ControlTheme>(Application.Current!.FindResource("KelpieCaptionButton"));
        Assert.DoesNotContain(captionTheme.Setters.OfType<Setter>(), s => s.Property == InputElement.FocusableProperty);
        Assert.All(buttons, b => Assert.Same(captionTheme, b.Theme));

        // No title text: the title bar shows ours.
        Assert.Empty(overlay.GetLogicalDescendants().OfType<TextBlock>());
        Assert.Null(content.Underlay);
    }

    [AvaloniaFact]
    public void TheTitleBarDragsAndItsControlsStayClickable()
    {
        var vm = new MainWindowViewModel(new GrpcLikeReviewSource(Fixture.CreateSource()), new AppSettings(),
            Path.Combine(Path.GetTempPath(), "lumen-app-tests", $"{Guid.NewGuid():N}.json"));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            Assert.Same(Application.Current!.FindResource("KelpieWindowDecorations"), window.WindowDecorationsTheme);
            var titleBar = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "TitleBar");
            Assert.Equal(WindowDecorationsElementRole.TitleBar, WindowDecorationProperties.GetElementRole(titleBar));
            Assert.NotNull(titleBar.Background);

            // Every control on the bar opts out of dragging; labels and the logo don't, so they drag.
            var controls = titleBar.GetVisualDescendants().Where(v => v is Button or TextBox).Cast<Control>().ToList();
            Assert.NotEmpty(controls);
            Assert.All(controls, c => Assert.Contains(c.GetSelfAndVisualAncestors().TakeWhile(a => a != titleBar),
                a => WindowDecorationProperties.GetElementRole(a) == WindowDecorationsElementRole.User));

            // The caption buttons' column matches the three 46 px buttons.
            Assert.Equal(138, titleBar.ColumnDefinitions[^1].Width.Value);
        }
        finally
        {
            window.Close();
        }
    }
}
