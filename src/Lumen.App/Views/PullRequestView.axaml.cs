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
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.FindAncestorOfType<Window>()?.DataContext is not MainWindowViewModel shell)
        {
            return;
        }

        // The right pane is off by default below about 1400px wide; the flags stay reachable from the slice header.
        if (_viewModel is { } vm && !_paneDefaulted)
        {
            _paneDefaulted = true;
            vm.IsRightPaneOpen = (this.FindAncestorOfType<Window>()?.Bounds.Width ?? 1600) >= RightPaneMinimumWidth;
        }

        if (shell.PaneWidths.Left is { } left)
        {
            Layout.ColumnDefinitions[0].Width = new GridLength(Math.Max(0, left));
        }

        // A closed pane keeps the saved width for when it opens; applying it now would reserve the empty column.
        if (shell.PaneWidths.Right is { } right)
        {
            if (_rightWidth is null)
            {
                Layout.ColumnDefinitions[4].Width = new GridLength(Math.Max(0, right));
            }
            else
            {
                _rightWidth = new GridLength(Math.Max(0, right));
            }
        }

        void Commit(object? sender, VectorEventArgs args) => shell.SavePaneWidths(Layout.ColumnDefinitions[0].ActualWidth, _rightWidth?.Value ?? Layout.ColumnDefinitions[4].ActualWidth);
        LeftSplitter.DragCompleted += Commit;
        RightSplitter.DragCompleted += Commit;
    }

    /// <summary>Below this window width the right pane starts closed.</summary>
    public const double RightPaneMinimumWidth = 1400;

    private bool _paneDefaulted;

    /// <summary>The right pane's width while it is collapsed, so opening it again restores it.</summary>
    private GridLength? _rightWidth;

    /// <summary>The right pane collapses to nothing; its width comes back when it opens.</summary>
    private void ApplyRightPane(bool open)
    {
        var right = Layout.ColumnDefinitions[4];
        if (!open && _rightWidth is null)
        {
            _rightWidth = right.Width;
            right.Width = new GridLength(0);
        }
        else if (open && _rightWidth is { } width)
        {
            right.Width = width;
            _rightWidth = null;
        }
    }

    private async Task OnMarkerClickedAsync(string id)
    {
        if (_viewModel is not null)
        {
            await _viewModel.GoToFlagAsync(id).ConfigureAwait(true);
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
            _viewModel.LineFocusRequested -= OnLineFocusRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.DraftOpened -= OnDraftOpened;
            _viewModel.LineRevealRequested -= OnLineRevealRequested;
            _viewModel.FindMatchRequested -= OnFindMatchRequested;
            _viewModel.ChangeStepRequested -= OnChangeStepRequested;
        }

        _viewModel = DataContext as PullRequestViewModel;
        if (_viewModel is not null)
        {
            _viewModel.LineFocusRequested += OnLineFocusRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.DraftOpened += OnDraftOpened;
            _viewModel.LineRevealRequested += OnLineRevealRequested;
            _viewModel.FindMatchRequested += OnFindMatchRequested;
            _viewModel.ChangeStepRequested += OnChangeStepRequested;
            ApplyRightPane(_viewModel.IsRightPaneOpen);
        }
    }

    /// <summary>Folds, red flags, threads and comment drafts each have their own card (plan v2 M3, M4).</summary>
    private Control? CreateCard(string cardId)
    {
        if (cardId.StartsWith(Diff.DiffFold.CardPrefix, StringComparison.Ordinal))
        {
            return _viewModel?.ResolveFold(cardId) is { } fold ? new FoldBadgeView { DataContext = fold } : null;
        }

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

        return _viewModel.ResolveFlagCard(cardId) is { } flag ? new FlagCardView { DataContext = flag } : null;
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

    /// <summary>Scrolls the diff to a line (a fold's group member, a flag), once the file has laid out.</summary>
    private async void OnLineFocusRequested(object? sender, (int Line, bool OldSide) target)
    {
        _navigation?.Cancel();
        _navigation?.Dispose();
        _navigation = new CancellationTokenSource();
        var token = _navigation.Token;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render, token);
            if (_viewModel?.CurrentDiff is not { } diff)
            {
                return;
            }

            var documentLine = target.OldSide
                ? diff.Rows.Select((row, index) => (row, index)).Where(x => x.row.OldNumber >= target.Line).Select(x => (int?)(x.index + 1)).FirstOrDefault()
                : diff.DocumentLineForNewLine(target.Line);
            if (documentLine is { } line)
            {
                if (_viewModel.IsSplitView)
                {
                    await SplitEditor.RevealUnifiedLineAsync(line, App.Motion).ConfigureAwait(true);
                }
                else
                {
                    await Editor.RevealDocumentLineAsync(line, App.Motion, token).ConfigureAwait(true);
                }
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
            case nameof(PullRequestViewModel.IsRightPaneOpen):
                ApplyRightPane(_viewModel.IsRightPaneOpen);
                break;

            case nameof(PullRequestViewModel.Toast) when _viewModel.Toast is not null:
                _toast?.Cancel();
                _toast = new CancellationTokenSource();
                var token = _toast.Token;
                await App.Motion.RevealAsync(this.FindControl<Border>("ToastHost")!, Motion.MotionPreset.Reveal, token).ConfigureAwait(true);
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
        }
    }

    public void Dispose()
    {
        _navigation?.Dispose();
        _toast?.Dispose();
    }
}
