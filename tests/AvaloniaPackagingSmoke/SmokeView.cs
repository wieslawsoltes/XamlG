using Avalonia.Controls;

namespace AvaloniaPackagingSmoke;

public partial class SmokeView : StackPanel
{
    public SmokeView() => InitializeComponent();
    public TextBlock Output => output;
    public TextBox Input => input;
    public Button Action => action;
}
