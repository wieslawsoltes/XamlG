using XamlG.Playground.Components;

namespace XamlG.Playground;

public partial class App
{
    private ProjectResources? _resourceEditor;
    private async Task ResourceChangedAsync()
    {
        _status = "Project resource changed · compile to update all dependent documents";
        await SaveDraftAsync();
    }
    private async Task LoadResourceExampleAsync()
    {
        if (_busy) return;
        Compiler.Resources.ReplaceAll(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Resources/Palette.axaml"] = """
                <ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <SolidColorBrush x:Key="Accent" Color="#5261D8" />
                </ResourceDictionary>
                """,
            ["Styles/Buttons.axaml"] = """
                <Styles xmlns="https://github.com/avaloniaui">
                  <Style Selector="Button.primary">
                    <Setter Property="Width" Value="240" />
                    <Setter Property="Height" Value="48" />
                  </Style>
                </Styles>
                """
        });
        _document = new("""
            <StackPanel xmlns="https://github.com/avaloniaui" Margin="32" Spacing="18">
              <StackPanel.Resources>
                <ResourceDictionary>
                  <ResourceDictionary.MergedDictionaries>
                    <MergeResourceInclude Source="Resources/Palette.axaml" />
                  </ResourceDictionary.MergedDictionaries>
                </ResourceDictionary>
              </StackPanel.Resources>
              <StackPanel.Styles>
                <StyleInclude Source="Styles/Buttons.axaml" />
              </StackPanel.Styles>
              <TextBlock Text="One project. Three compiled documents." FontSize="22" FontWeight="Bold" />
              <Button Classes="primary" Background="{StaticResource Accent}" Foreground="White" Content="Statically linked resources" />
            </StackPanel>
            """, "View.axaml");
        _code = "// Optional application types. All XAML resources compile into one assembly.\n";
        _selectedElement = null; _error = null; _result = null;
        await SaveDraftAsync(); await CompileSnapshotAsync();
    }
}
