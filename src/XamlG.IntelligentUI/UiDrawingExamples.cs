namespace XamlG.IntelligentUI;

/// <summary>Executable authoring example shared by agent discovery, native rendering and export tests.</summary>
public static class UiDrawingExamples
{
    public static UiPublish LayoutAndDrawing() => new("drawing", 0, 1, """
        <Grid xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui"
              ui:Key="viewport" Width="640" Height="320" RowDefinitions="Auto,*" Background="White">
          <Label ui:Key="title" Grid.Row="0" Content="Validated vector drawing"
                 FontSize="24" FontWeight="SemiBold" Foreground="Black" Padding="12,8"/>
          <ScrollViewer ui:Key="scroll" Grid.Row="1" HorizontalScrollBarVisibility="Auto"
                        VerticalScrollBarVisibility="Auto" AllowAutoHide="False">
            <Canvas ui:Key="canvas" Width="800" Height="500" Background="White">
              <Path ui:Key="curve" Canvas.Left="24" Canvas.Top="24" Width="240" Height="180"
                    Data="M10,90 C40,10 80,10 120,90 S200,170 230,90"
                    Stroke="Blue" StrokeThickness="3" StrokeDashArray="4,2"
                    StrokeLineCap="Round" StrokeJoin="Round" Stretch="Uniform"
                    AutomationProperties.Name="Blue cubic curve"/>
              <Polygon ui:Key="triangle" Canvas.Left="300" Canvas.Top="24" Width="160" Height="160"
                       Points="0,150 75,0 150,150" Fill="Orange" Stroke="Black"
                       StrokeThickness="2" FillRule="NonZero" Stretch="Uniform"
                       RenderTransform="matrix(1,0.15,0,1,8,0)" RenderTransformOrigin="50%,50%"
                       AutomationProperties.Name="Transformed orange triangle"/>
              <Polyline ui:Key="trend" Canvas.Left="24" Canvas.Top="240" Width="240" Height="100"
                        Points="0,80 60,45 120,65 180,15 240,30" Stroke="Green"
                        StrokeThickness="3" StrokeJoin="Round" Stretch="Uniform"
                        Clip="M0,0 L240,0 240,100 0,100 Z" AutomationProperties.Name="Green trend"/>
              <LayoutTransformControl ui:Key="rotated-caption" Canvas.Left="520" Canvas.Top="24"
                                      LayoutTransform="matrix(0,1,-1,0,0,0)">
                <Label ui:Key="caption" Content="Layout-aware rotation" Foreground="Black"
                       FontSize="18" Padding="4"/>
              </LayoutTransformControl>
            </Canvas>
          </ScrollViewer>
        </Grid>
        """);
}
