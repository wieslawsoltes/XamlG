namespace XamlG.Playground;

public static class PlaygroundExamples
{
    public static IReadOnlyList<PlaygroundExample> All { get; } = new[]
    {
        new PlaygroundExample("Welcome", "Direct C# construction, names, layout and literal conversion.", """
            <Border xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Padding="32" Background="#F4F6FB" CornerRadius="16">
              <StackPanel Spacing="16" VerticalAlignment="Center">
                <TextBlock x:Name="title" Text="Made of XAML. Compiled to C#."
                           FontSize="27" FontWeight="Bold" Foreground="#17213A" />
                <TextBlock Text="One compiler. Every host. Inspect the syntax, binding and generated output."
                           TextWrapping="Wrap" FontSize="15" Foreground="#516079" />
                <Border Background="#E1E8FF" Padding="16" CornerRadius="8">
                  <TextBlock Text="Edit a property to update the live preview." Foreground="#324FAC" />
                </Border>
                <Button Content="A real Avalonia button" HorizontalAlignment="Left" Padding="18,10" />
              </StackPanel>
            </Border>
            """),
        new PlaygroundExample("Names and events", "Generated partial class, named fields and a private event handler.", """
            <StackPanel xmlns="https://github.com/avaloniaui"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        x:Class="Playground.CounterView" Spacing="16" Margin="32">
              <TextBlock x:Name="counter" Text="0 clicks" FontSize="28" />
              <Button Content="Increment" Click="OnIncrement" />
            </StackPanel>
            """, """
            using Avalonia.Controls;
            using Avalonia.Interactivity;
            namespace Playground;
            public partial class CounterView : StackPanel
            {
                private int _count;
                public CounterView() => InitializeComponent();
                private void OnIncrement(object? sender, RoutedEventArgs args)
                    => counter.Text = $"{++_count} clicks";
            }
            """),
        new PlaygroundExample("Resources", "Static resources resolved through the generated service-provider parent stack.", """
            <StackPanel xmlns="https://github.com/avaloniaui"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        Margin="32" Spacing="16">
              <StackPanel.Resources>
                <SolidColorBrush x:Key="accent" Color="#5261D8" />
              </StackPanel.Resources>
              <Border Background="{StaticResource accent}" Padding="24" CornerRadius="12">
                <TextBlock Text="A resource, not a string substitution." Foreground="White" FontSize="20" />
              </Border>
            </StackPanel>
            """),
        new PlaygroundExample("Binding", "Real Avalonia BindingBase assignments, compiled into direct adapter calls.", """
            <StackPanel xmlns="https://github.com/avaloniaui" Margin="32" Spacing="16">
              <StackPanel.DataContext>
                <x:String xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">Hello from the data context</x:String>
              </StackPanel.DataContext>
              <TextBlock Text="{Binding}" FontSize="24" />
              <TextBlock Text="This text is produced by Avalonia's binding engine." />
            </StackPanel>
            """)
    };
}
