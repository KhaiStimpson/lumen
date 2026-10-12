using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Lumen.App.ViewModels;

namespace Lumen.App.Views;

public sealed partial class CommitsView : UserControl
{
    // Buttons swallow the press, so the modifiers are read on the way down.
    private KeyModifiers _modifiers;

    public CommitsView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, (_, e) => _modifiers = e.KeyModifiers, RoutingStrategies.Tunnel);
    }

    private async void OnCommitClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CommitViewModel commit } && DataContext is PullRequestViewModel pr)
        {
            await pr.SelectCommitAsync(commit, _modifiers.HasFlag(KeyModifiers.Shift)).ConfigureAwait(true);
        }
    }
}
