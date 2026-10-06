using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace XamlG.ThemeCorpus;

internal static class ThemeControlAcceptance
{
    public static int Realize(CancellationToken cancellationToken)
    {
        var factories = new Func<TemplatedControl>[]
        {
            () => new Button { Content = "Action" },
            () => new CheckBox { Content = "Choice", IsChecked = true },
            () => new RadioButton { Content = "Option", IsChecked = true },
            () => new TextBox { Text = "Editable text" },
            () => new ToggleSwitch { IsChecked = true },
            () => new Slider { Value = 40 },
            () => new ProgressBar { Value = 40 },
            () => new ListBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 },
            () => new ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 },
            () => new TabControl { ItemsSource = new[] { "First", "Second" }, SelectedIndex = 0 },
            () => new TreeView { ItemsSource = new[] { "Root" } },
            () => new ScrollViewer { Content = new TextBlock { Text = "Scrollable content" } },
            () => new Expander { Header = "Details", Content = new TextBlock { Text = "Expanded" }, IsExpanded = true },
            () => new NumericUpDown { Value = 12 },
            () => new DatePicker(),
            () => new TimePicker(),
            () => new Calendar()
        };
        var count = 0;
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Application.Current!.RequestedThemeVariant = variant;
            foreach (var create in factories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var control = create();
                var window = new Window { Width = 640, Height = 480, Content = control };
                try
                {
                    window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                    if (control.Template == null || !control.GetVisualDescendants().Any())
                        throw new InvalidOperationException($"{control.GetType().FullName} did not realize its compiled template in {variant}.");
                    if (!double.IsFinite(control.Bounds.Width) || !double.IsFinite(control.Bounds.Height))
                        throw new InvalidOperationException($"{control.GetType().FullName} produced invalid layout in {variant}.");
                    Console.WriteLine($"REALIZED {variant}: {control.GetType().Name}");
                    count++;
                }
                finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
            }
        }
        return count;
    }
}
