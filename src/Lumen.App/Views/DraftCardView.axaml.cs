using Avalonia.Controls;

namespace Lumen.App.Views;

public sealed partial class DraftCardView : UserControl
{
    public DraftCardView()
    {
        InitializeComponent();
    }

    public TextBox ComposerBox => this.FindControl<TextBox>("Composer")!;
}
