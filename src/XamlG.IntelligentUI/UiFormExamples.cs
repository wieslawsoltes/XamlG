using System.Text.Json;

namespace XamlG.IntelligentUI;

/// <summary>Forms use ordinary state slots and reviewed actions; validity is not a permission grant.</summary>
public static class UiFormExamples
{
    public static UiPublish Configuration(string id = "configuration") => new(id, 0, 1,
        """
        <ui:Form xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui"
                 Title="Review configuration" Description="Validation runs locally. Submission remains a reviewed action.">
          <ui:Field Label="Project" IsRequired="True" ErrorText="Enter a project name.">
            <TextBox ui:Key="project-input" ui:Bind="project" MaxLength="80" PlaceholderText="Project name"/>
          </ui:Field>
          <ui:Field Label="Seats" IsRequired="True" IsValid="{ui:Expr state.seats &lt;= data.limit}"
                    ErrorText="The seat limit is exceeded." HelpText="{ui:Expr &quot;Available seats: &quot; + data.limit}">
            <NumericUpDown ui:Key="seat-input" ui:Bind="seats" Minimum="1" Maximum="100"/>
          </ui:Field>
          <ui:Field Label="Confirmation" IsRequired="True" ErrorText="Review the configuration first.">
            <CheckBox ui:Key="approval-input" ui:Bind="approved" Content="I checked these values"/>
          </ui:Field>
          <ui:ValidationSummary ui:Key="errors"/>
          <StackPanel Orientation="Horizontal" Spacing="8">
            <ui:SubmitButton ui:Key="submit" ui:Action="submit" Content="Use configuration"/>
            <Button ui:Key="reset" ui:Action="reset" Content="Reset"/>
          </StackPanel>
        </ui:Form>
        """, JsonSerializer.SerializeToElement(new { project = "", seats = 1, approved = false, submitted = false }),
        JsonSerializer.SerializeToElement(new { limit = 5 }),
        [
            new("submit", "message", Text: "{ui:Expr \"Use project \" + state.project + \" with \" + state.seats + \" seats.\"}"),
            new("reset", "state", Arguments: JsonSerializer.SerializeToElement(new { project = "", seats = 1, approved = false, submitted = false }))
        ], "Interactive configuration with local validation.");
}
