using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Lumen.App.Views;

public sealed partial class OpenPullRequestView : UserControl
{
    public OpenPullRequestView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        this.FindControl<TextBox>("Reference")?.Focus();
    }
}
