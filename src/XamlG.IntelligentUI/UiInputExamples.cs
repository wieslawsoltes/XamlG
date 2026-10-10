using System.Text.Json;

namespace XamlG.IntelligentUI;

/// <summary>Native input authoring with explicit state actions and typed selector scopes.</summary>
public static class UiInputExamples
{
    public static UiPublish Controls() => new("input-authoring", 0, 1, """
        <Grid xmlns="https://github.com/avaloniaui"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
              xmlns:ui="urn:xamlg:intelligent-ui" ui:Key="root"
              RowDefinitions="Auto,*" Height="440" Margin="12">
          <Grid.Resources>
            <ControlTheme x:Key="primary" TargetType="Button">
              <Setter Property="Background" Value="Navy"/>
              <Setter Property="Foreground" Value="White"/>
              <Setter Property="Padding" Value="12,8"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate>
                <Border Name="chrome" Background="{TemplateBinding Background}"
                        Padding="{TemplateBinding Padding}" BorderThickness="2" CornerRadius="6">
                  <ContentPresenter Name="PART_ContentPresenter" Content="{TemplateBinding Content}"/>
                </Border>
              </ControlTemplate></Setter.Value></Setter>
            </ControlTheme>
          </Grid.Resources>
          <Grid.Styles>
            <Style Selector=":is(Button)"><Setter Property="FontSize" Value="15"/></Style>
            <Style Selector="Button /template/ Border#chrome"><Setter Property="BorderBrush" Value="Teal"/></Style>
            <Style Selector="StackPanel.summary > TextBlock:nth-child(2)"><Setter Property="Foreground" Value="Blue"/></Style>
            <Style Selector="StackPanel.summary TextBlock:not(.muted)"><Setter Property="FontWeight" Value="Medium"/></Style>
            <Style Selector="TextBlock, Label"><Setter Property="Margin" Value="0,2"/></Style>
          </Grid.Styles>
          <StackPanel Classes="summary" ui:Key="summary">
            <TextBlock Text="Keyboard and input authoring" FontSize="22"/>
            <TextBlock ui:Key="saved-label" Text="{Binding state.saved, StringFormat='Saved: {0}'}"/>
            <TextBlock Classes="muted" Text="{Binding state.name, StringFormat='Name: {0}'}"/>
          </StackPanel>
          <ScrollViewer Grid.Row="1">
            <StackPanel Spacing="8" KeyboardNavigation.TabNavigation="Local">
              <TextBox ui:Key="name" Text="{Binding state.name, Mode=TwoWay}" TabIndex="0"
                       CaretIndex="4" AutomationProperties.AutomationId="profile-name"
                       AutomationProperties.HelpText="Enter a name; Enter saves the current draft."/>
              <CheckBox ui:Key="choice" IsChecked="{Binding state.choice, Mode=TwoWay}"
                        IsThreeState="True" Content="Optional choice" TabIndex="1"/>
              <TextBlock Text="{ui:Expr state.choice == null ? &quot;Mixed choice&quot; : state.choice ? &quot;Enabled choice&quot; : &quot;Disabled choice&quot;}"/>
              <Slider ui:Key="amount" Value="{Binding state.amount, Mode=TwoWay}" Minimum="0" Maximum="100"
                      SmallChange="2" LargeChange="15" IsDirectionReversed="True" TabIndex="2"/>
              <TextBlock Text="{Binding state.amount, StringFormat='Amount: {0}'}"/>
              <StackPanel Orientation="Horizontal" Spacing="8">
                <Button ui:Key="save" ui:Action="save" Content="Save" IsDefault="True" Theme="{StaticResource primary}"/>
                <Button ui:Key="cancel" ui:Action="cancel" Content="Cancel" IsCancel="True"/>
                <RepeatButton ui:Key="repeat" ui:Action="increment" Content="Hold to add" Delay="400" Interval="100"/>
              </StackPanel>
              <TextBlock Text="{ui:Expr state.cancelled ? &quot;Cancelled locally&quot; : &quot;Ready&quot;}"/>
            </StackPanel>
          </ScrollViewer>
        </Grid>
        """, JsonSerializer.SerializeToElement(new { name = "Example", choice = (bool?)null, amount = 50, saved = 0, cancelled = false }),
        Actions:
        [
            new("save", "state", Arguments: JsonSerializer.SerializeToElement(new { saved = "{ui:Expr state.saved + 1}", cancelled = false })),
            new("cancel", "state", Arguments: JsonSerializer.SerializeToElement(new { cancelled = true })),
            new("increment", "state", Arguments: JsonSerializer.SerializeToElement(new { amount = "{ui:Expr Math.Min(100, state.amount + 2)}" }))
        ]);
}
