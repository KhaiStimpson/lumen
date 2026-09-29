using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Lumen.App.ViewModels;

namespace Lumen.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Escape hands focus back to the window so review shortcuts work again (windows aren't focusable by default).
        Focusable = true;

        // Tunnel so review shortcuts win over the read-only editor, but never over text input.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // The title bar is ours (extended client area): empty space drags, double-click maximises.
        TitleBar.PointerPressed += (_, e) =>
        {
            if (e.Source is not (Grid or StackPanel or TextBlock) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
            else
            {
                BeginMoveDrag(e);
            }
        };

        // A click outside the card closes it.
        SettingsHost.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual target && !SettingsCard.IsVisualAncestorOf(target) && target != SettingsCard)
            {
                ViewModel?.CloseSettingsCommand.Execute(null);
            }
        };
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    /// <summary>Keyboard-first review (TDD §29).</summary>
    private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // The Settings card is modal: Escape closes it, and review shortcuts stay with the view underneath.
        if (ViewModel is { IsSettingsOpen: true } main)
        {
            if (e.Key == Key.Escape)
            {
                main.CloseSettingsCommand.Execute(null);
                Focus();
                e.Handled = true;
            }

            return;
        }

        if (ViewModel?.PullRequest is not { } pr)
        {
            return;
        }

        var typing = FocusManager?.GetFocusedElement() is TextBox;
        var point = pr.CurrentPoint;

        if (e.Key == Key.K && e.KeyModifiers == KeyModifiers.Control)
        {
            this.FindControl<TextBox>("GoToFile")?.Focus();
            e.Handled = true;
            return;
        }

        if (typing)
        {
            if (e.Key == Key.Escape)
            {
                if (point is { IsComposing: true })
                {
                    pr.CancelComment(point);
                }

                if (FocusManager?.GetFocusedElement() is TextBox { Name: "GoToFile" } box)
                {
                    box.Text = "";
                }

                Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control && point is { IsComposing: true })
            {
                e.Handled = true;
                await pr.PostCommentAsync(point).ConfigureAwait(true);
            }

            return;
        }

        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        e.Handled = true;
        switch (e.Key)
        {
            case Key.J:
                await pr.NextPointAsync().ConfigureAwait(true);
                break;
            case Key.K:
                await pr.PreviousPointAsync().ConfigureAwait(true);
                break;
            case Key.Enter:
                await pr.ExamineAsync(null).ConfigureAwait(true);
                break;
            case Key.Escape when pr.IsExamining:
                pr.CloseExamine();
                break;
            case Key.D:
                await pr.DismissAsync(null).ConfigureAwait(true);
                break;
            case Key.A:
                await pr.AcknowledgeCurrentGroupAsync().ConfigureAwait(true);
                break;
            case Key.C:
                pr.StartComment(null);
                FocusComposer();
                break;
            case Key.P:
                await pr.ShowPrecedentAsync(null).ConfigureAwait(true);
                break;
            case Key.E:
                await pr.ShowEvidenceAsync(null).ConfigureAwait(true);
                break;
            case Key.F:
                this.FindControl<TextBox>("GoToFile")?.Focus();
                break;
            case Key.X:
                pr.ShowFullFile = !pr.ShowFullFile;
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private void FocusComposer() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Classes.Contains("composer") && t.IsEffectivelyVisible)?.Focus(),
            Avalonia.Threading.DispatcherPriority.Background);
}
