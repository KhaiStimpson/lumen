using Avalonia.Controls;
using Avalonia.Interactivity;
using Lumen.App.ViewModels;

namespace Lumen.App.Views;

public sealed partial class ReviewCardView : UserControl
{
    public ReviewCardView()
    {
        InitializeComponent();
    }

    public Control CardSurface => this.FindControl<Border>("Card")!;

    private async void OnOpenPosted(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CardContext { Point.PostedUrl: { } url } && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(new Uri(url)).ConfigureAwait(true);
        }
    }
}
