using System.Text.Json;

namespace XamlG.IntelligentUI;

/// <summary>Conventional XAML authoring using the same revisioned, data-only UI contract.</summary>
public static class UiAuthoringExamples
{
    public static UiPublish Directory() => new("authoring", 0, 1, """
        <Grid xmlns="https://github.com/avaloniaui"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
              xmlns:ui="urn:xamlg:intelligent-ui" RowDefinitions="Auto,*" Height="360">
          <Grid.Resources>
            <LinearGradientBrush x:Key="card" StartPoint="0%,0%" EndPoint="100%,100%" Opacity="0.2">
              <GradientStop Color="Blue" Offset="0"/><GradientStop Color="Teal" Offset="1"/>
            </LinearGradientBrush>
            <DataTemplate x:Key="person">
              <Border Background="{StaticResource card}" Padding="12" CornerRadius="8" Margin="0,4">
                <StackPanel Spacing="4">
                  <TextBlock Text="{Binding name}" FontWeight="Bold"/>
                  <TextBlock Text="{Binding score, StringFormat='Score: {0:F0}'}"/>
                  <Button Classes="choose" Content="Select" ui:Action="choose"/>
                </StackPanel>
              </Border>
            </DataTemplate>
          </Grid.Resources>
          <Grid.Styles>
            <Style Selector="Button.choose"><Setter Property="Padding" Value="12,6"/>
              <Style Selector="^:pointerover"><Setter Property="Opacity" Value="0.8"/></Style>
            </Style>
          </Grid.Styles>
          <StackPanel Spacing="4">
            <TextBlock Text="{Binding state.minimum, StringFormat='Minimum score: {0:F0}'}"/>
            <Slider Minimum="0" Maximum="100" Value="{Binding state.minimum, Mode=TwoWay}"/>
            <TextBlock Text="{Binding state.selected, StringFormat='Selected: {0}'}"/>
          </StackPanel>
          <ScrollViewer Grid.Row="1">
            <StackPanel>
              <ContentControl Content="{Binding featured}" ContentTemplate="{StaticResource person}"/>
              <ItemsControl ItemsSource="{ui:Expr data.people.Where(p => p.score >= state.minimum)}"
                            ui:ItemKey="{Binding id}" ItemTemplate="{StaticResource person}"/>
            </StackPanel>
          </ScrollViewer>
        </Grid>
        """, JsonSerializer.SerializeToElement(new { minimum = 50, selected = "none" }),
        JsonSerializer.SerializeToElement(new
        {
            featured = new { id = "featured", name = "Featured profile", score = 98 },
            people = new[] { new { id = "ada", name = "Ada", score = 95 }, new { id = "grace", name = "Grace", score = 85 }, new { id = "alan", name = "Alan", score = 45 } }
        }), [new("choose", "state", Arguments: JsonSerializer.SerializeToElement(new { selected = "{ui:Expr item.id}" }))]);
}
