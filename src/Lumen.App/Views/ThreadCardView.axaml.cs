using Avalonia.Controls;

namespace Lumen.App.Views;

public sealed partial class ThreadCardView : UserControl
{
    public ThreadCardView()
    {
        InitializeComponent();
    }

    public Control CardSurface => this.FindControl<Border>("Card")!;
}
