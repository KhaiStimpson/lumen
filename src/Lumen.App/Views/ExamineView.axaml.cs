using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Lumen.App.Diff;
using Lumen.App.Motion;
using Lumen.App.ViewModels;

namespace Lumen.App.Views;

public sealed partial class ExamineView : UserControl
{
    public ExamineView()
    {
        InitializeComponent();
    }

    /// <summary>Inline card → examine panel (TDD §25.2): the panel grows out of the card the reviewer chose.</summary>
    public async Task EnterAsync(Control? sourceCard)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        this.FindControl<ScrollViewer>("PanelScroll")!.Offset = default;

        var reveal = RevealCurrentLineAsync(CancellationToken.None);
        if (sourceCard is not null)
        {
            await App.Motion.TransitionAsync(sourceCard, Panel, MotionPreset.ExpandFinding, CancellationToken.None).ConfigureAwait(true);
        }
        else
        {
            await App.Motion.RevealAsync(Panel, MotionPreset.ExpandFinding, CancellationToken.None).ConfigureAwait(true);
        }

        await reveal.ConfigureAwait(true);
    }

    /// <summary>Next/previous while examining: the code scrolls, the evidence surface morphs to the new point.</summary>
    public async Task ShowPointAsync(NavigationRequest request, CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render, cancellationToken);
        this.FindControl<ScrollViewer>("PanelScroll")!.Offset = default;
        await Task.WhenAll(
            RevealCurrentLineAsync(cancellationToken),
            App.Motion.RevealAsync(this.FindControl<StackPanel>("PanelContent")!, MotionPreset.SwitchInvestigationSurface, cancellationToken)).ConfigureAwait(true);
    }

    private Task RevealCurrentLineAsync(CancellationToken cancellationToken)
    {
        if (DataContext is not PullRequestViewModel { CurrentPoint: { } point } pr)
        {
            return Task.CompletedTask;
        }

        var line = point.Locations.FirstOrDefault(l => l.Path == pr.SelectedFile?.Path)?.Line ?? point.Line;
        return Code.RevealLineAsync(line, App.Motion, cancellationToken);
    }
}
