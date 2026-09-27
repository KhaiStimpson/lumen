using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
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
        Editor.MarkerClicked += async (_, id) =>
        {
            if (_viewModel?.ReviewPoints.FirstOrDefault(p => p.Id == id) is { } point)
            {
                await _viewModel.SelectPointAsync(point).ConfigureAwait(true);
            }
        };

        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && _viewModel?.SourcePreview is not null)
            {
                _viewModel.CloseSource();
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.NavigationRequested -= OnNavigationRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as PullRequestViewModel;
        if (_viewModel is not null)
        {
            _viewModel.NavigationRequested += OnNavigationRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // The session may already have arrived at a point before this view existed; catch up.
            if (_viewModel.CurrentPoint is { } current)
            {
                OnNavigationRequested(_viewModel, new NavigationRequest(current, NavigationReason.Arrived, null));
            }
        }
    }

    private ReviewCardView? CreateCard(string cardId)
    {
        if (_viewModel?.ResolveCard(cardId) is not { } resolved)
        {
            return null;
        }

        return new ReviewCardView { DataContext = new CardContext(_viewModel, resolved.Point, resolved.IsPrimary) };
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

            if (request.From is { } from && from != request.Point && Editor.CardFor(from.Id) is { } leaving)
            {
                _ = motion.ContractAsync(leaving, token);
            }

            var line = request.Point.Locations.FirstOrDefault(l => l.Path == _viewModel.SelectedFile?.Path)?.Line ?? request.Point.Line;
            await Editor.RevealLineAsync(line, request.Reason == NavigationReason.Arrived ? null : motion, token).ConfigureAwait(true);

            if (Editor.CardFor(request.Point.Id) is ReviewCardView arriving && request.Reason != NavigationReason.Refocus)
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
            case nameof(PullRequestViewModel.Mode) when _viewModel.IsExamining:
                var source = _viewModel.CurrentPoint is { } current && Editor.CardFor(current.Id) is ReviewCardView card && card.IsEffectivelyVisible
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
