using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Lumen.App.ViewModels;
using Avalonia.Controls.Primitives;

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

        // Back in the foreground: the inbox refreshes (plan v2 M1: polled, and on focus).
        Activated += async (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                await vm.OnActivatedAsync().ConfigureAwait(true);
            }
        };

        // "Open on GitHub" from the palette.
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                vm.OpenUrlRequested += async (_, url) =>
                {
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        await Launcher.LaunchUriAsync(uri).ConfigureAwait(true);
                    }
                };
            }
        };

        // The palette closes when its box loses focus (after a click on an item has run).
        AddHandler(LostFocusEvent, (_, e) =>
        {
            if (e.Source is TextBox { Name: "GoToFile" } && ViewModel is { } vm)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (FocusManager?.GetFocusedElement() is not TextBox { Name: "GoToFile" } && !IsWithinPalette())
                        {
                            vm.Palette.IsOpen = false;
                        }
                    },
                    Avalonia.Threading.DispatcherPriority.Background);
            }
        }, RoutingStrategies.Bubble);

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

    private bool IsWithinPalette() =>
        FocusManager?.GetFocusedElement() is Visual focused && this.FindControl<Avalonia.Controls.Primitives.Popup>("PalettePopup")?.Child is { } child
        && (focused == child || child.IsVisualAncestorOf(focused));

    /// <summary>Keyboard-first review (TDD §29, plan v2: every new action has a key).</summary>
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

        if (ViewModel is not { } vm)
        {
            return;
        }

        var focused = FocusManager?.GetFocusedElement() as TextBox;
        if (e.Key == Key.K && e.KeyModifiers == KeyModifiers.Control)
        {
            OpenPalette();
            e.Handled = true;
            return;
        }

        // The palette's box: arrows pick, Enter runs, Escape leaves.
        if (focused?.Name == "GoToFile")
        {
            e.Handled = true;
            switch (e.Key)
            {
                case Key.Down:
                    vm.Palette.Move(+1);
                    break;
                case Key.Up:
                    vm.Palette.Move(-1);
                    break;
                case Key.Enter:
                    Focus();
                    await vm.Palette.RunCommand.ExecuteAsync(null).ConfigureAwait(true);
                    break;
                case Key.Escape:
                    vm.Palette.Close();
                    focused.Text = "";
                    Focus();
                    break;
                default:
                    e.Handled = false;
                    break;
            }

            return;
        }

        if (vm.PullRequest is not { } pr)
        {
            return;
        }

        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            pr.ShowTab(ViewModels.PullRequestTab.Files);
            pr.OpenFind();
            e.Handled = true;
            return;
        }

        if (focused is not null)
        {
            e.Handled = await OnTypingKeyAsync(pr, focused, e).ConfigureAwait(true);
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Shift && e.Key == Key.N)
        {
            e.Handled = true;
            await pr.PreviousThreadAsync().ConfigureAwait(true);
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
            case Key.Escape when pr.IsFindOpen:
                pr.CloseFind();
                break;
            case Key.Escape when pr.IsExamining:
                pr.CloseExamine();
                break;
            case Key.Escape when pr.IsTrayOpen:
                pr.IsTrayOpen = false;
                break;
            case Key.D:
                await pr.DismissAsync(null).ConfigureAwait(true);
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
                OpenPalette();
                break;
            case Key.X:
                pr.ShowFullFile = !pr.ShowFullFile;
                break;
            case Key.N:
                await pr.NextThreadAsync().ConfigureAwait(true);
                break;
            case Key.R:
                pr.ToggleTray();
                if (pr.IsTrayOpen)
                {
                    FocusNamed("ReviewSummary");
                }

                break;
            case Key.S:
                pr.ToggleSplitView();
                break;
            case Key.W:
                pr.ToggleIgnoreWhitespace();
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    /// <summary>
    /// In a text box only Ctrl+Enter, Ctrl+Shift+Enter and Escape mean anything, and what they mean depends on the box:
    /// a review point's or line's comment adds to the review (Ctrl+Enter) or posts at once (Ctrl+Shift+Enter); a
    /// reply or a conversation comment posts; Escape cancels and hands the keyboard back.
    /// </summary>
    private async Task<bool> OnTypingKeyAsync(ViewModels.PullRequestViewModel pr, TextBox box, KeyEventArgs e)
    {
        var context = box.FindAncestorOfType<UserControl>()?.DataContext;
        var ctrlEnter = e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control;
        var ctrlShiftEnter = e.Key == Key.Enter && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift);

        if (box.Name == "FindBox")
        {
            switch (e.Key)
            {
                case Key.Enter when e.KeyModifiers == KeyModifiers.Shift:
                    pr.FindPrevious();
                    return true;
                case Key.Enter:
                    pr.FindNext();
                    return true;
                case Key.Escape:
                    pr.CloseFind();
                    Focus();
                    return true;
                default:
                    return false;
            }
        }

        if (e.Key == Key.Escape)
        {
            switch (context)
            {
                case DraftCardContext draft:
                    pr.CancelDraft(draft.Draft);
                    break;
                case ThreadCardContext thread:
                    ViewModels.PullRequestViewModel.CancelReply(thread.Thread);
                    break;
                case CardContext or ViewModels.PullRequestViewModel when box.Name is not ("IssueComment" or "ReviewSummary")
                                                                    && pr.CurrentPoint is { IsComposing: true } current:
                    pr.CancelComment(current);
                    break;
            }

            Focus();
            return true;
        }

        if (!ctrlEnter && !ctrlShiftEnter)
        {
            return false;
        }

        switch (context)
        {
            case DraftCardContext draft:
                await (ctrlShiftEnter ? pr.PostDraftNowAsync(draft.Draft) : pr.AddDraftToReviewAsync(draft.Draft)).ConfigureAwait(true);
                return true;
            case ThreadCardContext thread:
                await pr.ReplyAsync(thread.Thread).ConfigureAwait(true);
                return true;
        }

        if (box.Name == "IssueComment")
        {
            await pr.PostIssueCommentAsync().ConfigureAwait(true);
            return true;
        }

        if (box.Name == "ReviewSummary")
        {
            await pr.SubmitCommentAsync().ConfigureAwait(true);
            return true;
        }

        if (pr.CurrentPoint is { IsComposing: true } point)
        {
            await (ctrlShiftEnter ? pr.PostCommentAsync(point) : pr.AddPointCommentToReviewAsync(point)).ConfigureAwait(true);
            return true;
        }

        return false;
    }

    private void OpenPalette()
    {
        ViewModel?.Palette.Open();
        this.FindControl<TextBox>("GoToFile")?.Focus();
    }

    private void FocusNamed(string name) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == name && t.IsEffectivelyVisible)?.Focus(),
            Avalonia.Threading.DispatcherPriority.Background);

    private void FocusComposer() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Classes.Contains("composer") && t.IsEffectivelyVisible)?.Focus(),
            Avalonia.Threading.DispatcherPriority.Background);
}
