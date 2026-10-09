using XamlG.Automation;

namespace XamlG.IntelligentUI;

/// <summary>Owner-reviewed full C# examples. Numeric state can retain decimal scale after native
/// input; GetDecimal and an explicit checked conversion express the expected integral domain.</summary>
public static class UiCSharpExamples
{
    public static UiPublish Sum(string id = "full-query") => new(id, 0, 1, """
        <StackPanel xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui" Spacing="12">
          <Slider Width="300" Height="40" HorizontalAlignment="Left" ui:Bind="n"
                  Minimum="1" Maximum="20" IsSnapToTickEnabled="True" TickFrequency="1"/>
          <TextBlock Text="{ui:Expr &quot;Sum: &quot; + Enumerable.Range(1, checked((int)state.GetProperty(&quot;n&quot;).GetDecimal())).Sum()}"/>
        </StackPanel>
        """, AutomationJson.Element(new { n = 3 }), AutomationJson.Element(new { }), []);
}
