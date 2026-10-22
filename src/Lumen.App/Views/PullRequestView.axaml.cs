using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Lumen.App.Diff;
using Lumen.App.ViewModels;

namespace Lumen.App.Views;

public sealed partial class PullRequestView : UserControl, IDisposable
{
    private PullRequestViewModel? _viewModel;
    private CancellationTokenSource? _navigation;
    private CancellationTokenSource? _toast;

    public PullRequestView()
    {
        InitializeComponent();
        Editor.CardFactory = CreateCard;
        SplitEditor.CardFactory = CreateCard;
        Editor.MarkerClicked += async (_, id) => await OnMarkerClickedAsync(id).ConfigureAwait(true);
        SplitEditor.MarkerClicked += async (_, id) => await OnMarkerClickedAsync(id).ConfigureAwait(true);
        Editor.LineCommentRequested += (_, request) => _viewModel?.StartLineComment(request.Side, request.Line, request.StartLine);
        SplitEditor.LineCommentRequested += (_, request) => _viewModel?.StartLineComment(request.Side, request.Line, request.StartLine);
        Editor.CurrentChangeChanged += (_, index) => _viewModel?.SetCurrentChange(index);
        SplitEditor.Right.CurrentChangeChanged += (_, index) => _viewModel?.SetCurrentChange(index);

        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && _viewModel?.SourcePreview is not null)
            {
                _viewModel.CloseSource();
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.FindAncestorOfType<Window>()?.DataContext is not MainWindowViewModel shell)
        {
            return;
        }

        if (shell.PaneWidths.Left is { } left)
        {
            Layout.ColumnDefinitions[0].Width = new GridLength(Math.Max(0, left));
        }

        if (shell.PaneWidths.Right is { } right)
        {
            Layout.ColumnDefinitions[4].Width = new GridLength(Math.Max(0, right));
        }

        void Commit(object? sender, VectorEventArgs args) => shell.SavePaneWidths(Layout.ColumnDefinitions[0].ActualWidth, _rightBeforeMap?.Value ?? Layout.ColumnDefinitions[4].ActualWidth);
        LeftSplitter.DragCompleted += Commit;
        RightSplitter.DragCompleted += Commit;
    }

    /// <summary>The Map is an overview: it takes the walkthrough pane's width while it shows, and gives it back after.</summary>
    private void ApplyMapPanes(bool map)
    {
        var right = Layout.ColumnDefinitions[4];
        if (map && _rightBeforeMap is null)
        {
            _rightBeforeMap = right.Width;
            right.Width = new GridLength(0);
        }
        else if (!map && _rightBeforeMap is { } width)
        {
            right.Width = width;
            _rightBeforeMap = null;
        }
    }

    private GridLength? _rightBeforeMap;

    private async Task OnMarkerClickedAsync(string id)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_viewModel.ReviewPoints.FirstOrDefault(p => p.Id == id) is { } point)
        {
            await _viewModel.SelectPointAsync(point).ConfigureAwait(true);
        }
        else if (_viewModel.Walkthrough?.Risks.FirstOrDefault(r => r.MarkerId == id) is { } risk)
        {
            await _viewModel.SelectRiskAsync(risk).ConfigureAwait(true);
        }
    }

    /// <summary>The editor on screen: unified, or the head side of the split view.</summary>
    private DiffEditor ActiveEditor => _viewModel?.IsSplitView == true ? SplitEditor.Right : Editor;

    private Control? ExistingCard(string id) => _viewModel?.IsSplitView == true ? SplitEditor.CardFor(id) : Editor.CardFor(id);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.NavigationRequested -= OnNavigationRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.DraftOpened -= OnDraftOpened;
            _viewModel.LineRevealRequested -= OnLineRevealRequested;
            _viewModel.FindMatchRequested -= OnFindMatchRequested;
            _viewModel.ChangeStepRequested -= OnChangeStepRequested;
        }

        _viewModel = DataContext as PullRequestViewModel;
        if (_viewModel is not null)
        {
            _viewModel.NavigationRequested += OnNavigationRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.DraftOpened += OnDraftOpened;
            _viewModel.LineRevealRequested += OnLineRevealRequested;
            _viewModel.FindMatchRequested += OnFindMatchRequested;
            _viewModel.ChangeStepRequested += OnChangeStepRequested;

            // The session may already have arrived at a point before this view existed; catch up.
            if (_viewModel.CurrentPoint is { } current)
            {
                OnNavigationRequested(_viewModel, new NavigationRequest(current, NavigationReason.Arrived, null));
            }
        }
    }

    /// <summary>Review points, threads and comment drafts each have their own card (plan v2 M3, M4).</summary>
    private Control? CreateCard(string cardId)
    {
        if (_viewModel is null)
        {
            return null;
        }

        if (_viewModel.FindDraft(cardId) is { } draft)
        {
            return new DraftCardView { DataContext = new DraftCardContext(_viewModel, draft) };
        }

        if (_viewModel.FindThread(cardId) is { } thread)
        {
            return new ThreadCardView { DataContext = new ThreadCardContext(_viewModel, thread) };
        }

        return _viewModel.ResolveCard(cardId) is { } resolved
            ? new ReviewCardView { DataContext = new CardContext(_viewModel, resolved.Point, resolved.IsPrimary) }
            : null;
    }

    /// <summary>Focuses a new comment draft's composer once its card is on screen.</summary>
    private async void OnDraftOpened(object? sender, CommentDraftViewModel draft)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        if (_viewModel?.IsSplitView == true)
        {
            await SplitEditor.RevealCardAsync(draft.Id, null).ConfigureAwait(true);
        }
        else
        {
            await Editor.RevealCardAsync(draft.Id, null).ConfigureAwait(true);
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (ExistingCard(draft.Id) is DraftCardView card && card.IsEffectivelyVisible)
            {
                card.ComposerBox.Focus();
                return;
            }
        }
    }

    private async void OnLineRevealRequested(object? sender, LineRevealRequest request)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        var motion = App.Motion;
        if (request.CardId is { } card)
        {
            var found = _viewModel?.IsSplitView == true
                ? await SplitEditor.RevealCardAsync(card, motion).ConfigureAwait(true)
                : await Editor.RevealCardAsync(card, motion).ConfigureAwait(true);
            if (found)
            {
                return;
            }
        }

        if (_viewModel?.IsSplitView == true)
        {
            await SplitEditor.RevealLineAsync(request.Side, request.Line, motion).ConfigureAwait(true);
        }
        else
        {
            await Editor.RevealLineAsync(request.Side, request.Line, motion).ConfigureAwait(true);
        }
    }

    /// <summary>[ and ]: the next or previous change in this file, then on into the next or previous file.</summary>
    private async void OnChangeStepRequested(object? sender, int direction)
    {
        if (_viewModel is null || ActiveEditor.StepChange(direction, App.Motion))
        {
            return;
        }

        if (!await _viewModel.StepFileAsync(direction).ConfigureAwait(true))
        {
            return;
        }

        // Let the new file lay out, then land on its first change (or its last, going backwards).
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (ActiveEditor.Diff is { Changes.Count: > 0 } diff)
        {
            var index = direction > 0 ? 0 : diff.Changes.Count - 1;
            ActiveEditor.JumpTo(diff.Changes[index].StartLine, index);
        }
    }

    private async void OnFindMatchRequested(object? sender, Diff.FindMatch match)
    {
        if (_viewModel?.IsSplitView == true)
        {
            await SplitEditor.RevealUnifiedLineAsync(match.DocumentLine, null).ConfigureAwait(true);
        }
        else
        {
            await Editor.RevealDocumentLineAsync(match.DocumentLine, null).ConfigureAwait(true);
        }
    }

    private async void OnNavigationRequested(object? sender, NavigationRequest request)
    {
        if (_viewModel is null)
        {
            return;
        }

        _navigation?.Cancel();
        _navigation = new CancellationTokenSource();
        var token = _navigation.Token;
        var motion = App.Motion;

        try
        {
            if (_viewModel.IsExamining)
            {
                await Examine.ShowPointAsync(request, token).ConfigureAwait(true);
                return;
            }

            // Let the editor lay out a newly loaded file before measuring where to scroll.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

            if (request.From is { } from && from != request.Point && ExistingCard(from.Id) is { } leaving)
            {
                _ = motion.ContractAsync(leaving, token);
            }

            var line = request.Point.Locations.FirstOrDefault(l => l.Path == _viewModel.SelectedFile?.Path)?.Line ?? request.Point.Line;
            await ActiveEditor.RevealLineAsync(line, request.Reason == NavigationReason.Arrived ? null : motion, token).ConfigureAwait(true);

            if (ExistingCard(request.Point.Id) is ReviewCardView arriving && request.Reason != NavigationReason.Refocus)
            {
                await motion.RevealAsync(arriving.CardSurface, Motion.MotionPreset.NavigateReviewPoint, token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(PullRequestViewModel.Tab):
                ApplyMapPanes(_viewModel.IsMapTab);
                break;

            case nameof(PullRequestViewModel.Mode) when _viewModel.IsExamining:
                var source = _viewModel.CurrentPoint is { } current && ExistingCard(current.Id) is ReviewCardView card && card.IsEffectivelyVisible
                    ? card.CardSurface
                    : null;
                await Examine.EnterAsync(source).ConfigureAwait(true);
                break;

            case nameof(PullRequestViewModel.Mode):
                await App.Motion.RevealAsync(this.FindControl<Grid>("Center")!, Motion.MotionPreset.CollapseFinding, CancellationToken.None).ConfigureAwait(true);
                break;

            case nameof(PullRequestViewModel.Toast) when _viewModel.Toast is not null:
                _toast?.Cancel();
                _toast = new CancellationTokenSource();
                var token = _toast.Token;
                await App.Motion.RevealAsync(this.FindControl<Border>("ToastHost")!, Motion.MotionPreset.RevealEvidence, token).ConfigureAwait(true);
                try
                {
                    await Task.Delay(3200, token).ConfigureAwait(true);
                    _viewModel.Toast = null;
                }
                catch (OperationCanceledException)
                {
                }

                break;

            case nameof(PullRequestViewModel.IsFindOpen) when _viewModel.IsFindOpen:
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                this.FindControl<TextBox>("FindBox")?.Focus();
                break;

            case nameof(PullRequestViewModel.SourcePreview) when _viewModel.SourcePreview is { } preview:
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                var scroll = this.FindControl<ScrollViewer>("SourceScroll")!;
                scroll.Offset = new Avalonia.Vector(0, Math.Max(0, (preview.Line - preview.StartLine - 8) * 17.5));
                await App.Motion.RevealAsync(this.FindControl<Border>("SourceSheet")!, Motion.MotionPreset.OpenPrecedent, CancellationToken.None).ConfigureAwait(true);
                break;
        }
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender)
        {
            _viewModel?.CloseSource();
        }
    }

    public void Dispose()
    {
        _navigation?.Dispose();
        _toast?.Dispose();
    }
}
