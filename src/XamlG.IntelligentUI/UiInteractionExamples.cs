using System.Text.Json;

namespace XamlG.IntelligentUI;

/// <summary>Executable declarations shared by catalog discovery, hosts and regression tests.</summary>
public static class UiInteractionExamples
{
    public static UiPublish Counter(string id = "counter") => new(id, 0, 1,
        """
        <StackPanel xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui" Spacing="12">
          <TextBlock FontSize="24" FontWeight="Bold" Text="{ui:Expr &quot;Count: &quot; + state.count}"/>
          <TextBlock Text="{ui:Expr &quot;Previous: &quot; + state.previous}"/>
          <StackPanel Orientation="Horizontal" Spacing="8">
            <Button ui:Key="increment" ui:Action="increment" Content="Increment"/>
            <Button ui:Key="reset" ui:Action="reset" Content="Reset"/>
          </StackPanel>
        </StackPanel>
        """, JsonSerializer.SerializeToElement(new { count = 0, previous = 0 }),
        Actions:
        [
            new("increment", "state", Arguments: JsonSerializer.SerializeToElement(new
            { count = "{ui:Expr state.count + 1}", previous = "{ui:Expr state.count}" })),
            new("reset", "state", Arguments: JsonSerializer.SerializeToElement(new { count = 0, previous = 0 }))
        ], FallbackMarkdown: "Local counter. Increment and reset do not call a model.");
}
