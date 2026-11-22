using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Lumen.App.Views;

public sealed partial class ChecksView : UserControl
{
    public ChecksView()
    {
        InitializeComponent();
    }

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(uri).ConfigureAwait(true);
        }
    }
}
