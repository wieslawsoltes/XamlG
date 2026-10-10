using System.Text.Json;

namespace XamlG.IntelligentUI;

public static class UiRichExamples
{
    public static UiPublish Dashboard(string id = "dashboard") => new(id, 0, 1,
        """
        <ui:Card xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui" Title="Scenario dashboard" Gap="3">
          <ui:Paragraph>Change the multiplier. The table, chart and metric update locally.</ui:Paragraph>
          <Slider ui:Key="scale" ui:Bind="scale" Minimum="1" Maximum="5" IsSnapToTickEnabled="True" TickFrequency="1"/>
          <ui:Metric Title="Total" Value="{ui:Expr (data.points.Sum(p =&gt; p.value) * state.scale).ToString()}" Detail="Synthetic example data"/>
          <ui:BarChart Title="By category" PlotHeight="160">
            <ui:DataPoint ui:Key="point" ui:Each="{ui:Expr data.points}" ui:ItemKey="{ui:Expr item.id}" Label="{ui:Expr item.label}" Value="{ui:Expr item.value * state.scale}"/>
          </ui:BarChart>
          <ui:Table Columns="Category,Value" ColumnDefinitions="2*,*">
            <ui:TableRow ui:Key="row" ui:Each="{ui:Expr data.points}" ui:ItemKey="{ui:Expr item.id}">
              <TextBlock Text="{ui:Expr item.label}"/>
              <TextBlock Text="{ui:Expr (item.value * state.scale).ToString()}"/>
            </ui:TableRow>
          </ui:Table>
          <Button ui:Key="reset-scale" ui:Action="reset" Content="Reset scenario"/>
        </ui:Card>
        """, JsonSerializer.SerializeToElement(new { scale = 1 }),
        JsonSerializer.SerializeToElement(new { points = new[] { new { id = "a", label = "Alpha", value = 10 }, new { id = "b", label = "Beta", value = 25 }, new { id = "c", label = "Gamma", value = 15 } } }),
        [new("reset", "state", Arguments: JsonSerializer.SerializeToElement(new { scale = 1 }))],
        "Interactive scenario dashboard using synthetic data.");
}
