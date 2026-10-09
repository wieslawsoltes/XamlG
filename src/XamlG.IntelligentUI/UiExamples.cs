using XamlG.Automation;

namespace XamlG.IntelligentUI;

public static class UiExamples
{
    public static UiPublish Pricing(string id = "pricing") => new(id, 0, 1,
        """
        <Border xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui"
                Padding="16" BorderBrush="Gray" BorderThickness="1" CornerRadius="8">
          <StackPanel Spacing="12">
            <TextBlock Text="Interactive pricing example" FontSize="22" FontWeight="SemiBold"/>
            <TextBlock Text="Move the slider. The calculation runs locally without another model request." TextWrapping="Wrap"/>
            <Slider ui:Key="seats" ui:Bind="seats" Minimum="1" Maximum="50" TickFrequency="1" IsSnapToTickEnabled="True"/>
            <TextBlock ui:Key="price" Text="{ui:Expr &quot;$&quot; + state.seats * data.unitPrice + &quot;/month for &quot; + state.seats + &quot; seats&quot;}" FontSize="24"/>
            <CheckBox ui:Key="annual" ui:Bind="annual" Content="Annual billing (20% discount)"/>
            <TextBlock ui:Key="year" ui:When="{ui:Expr state.annual}" Text="{ui:Expr &quot;Annual total: $&quot; + state.seats * data.unitPrice * 12 * 0.8}"/>
            <Button ui:Key="continue" ui:Action="continue" Content="Discuss this configuration"/>
          </StackPanel>
        </Border>
        """,
        AutomationJson.Element(new { seats = 8, annual = false }), AutomationJson.Element(new { unitPrice = 29 }),
        [new("continue", "message", "{ui:Expr \"Plan an implementation for \" + state.seats + \" seats. Annual discount: \" + state.annual}")],
        "Example data only. Pricing is illustrative, not a product offer.");
}
