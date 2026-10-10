using System.Text.Json;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiFormValidationTests
{
    private const string Ns = "xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\"";
    private const string Field = "<ui:Field IsRequired=\"True\" ErrorText=\"NEEDS_A_VALUE\"><TextBox ui:Bind=\"value\"/></ui:Field>";
    private const string Submit = "<ui:SubmitButton ui:Key=\"submit\" ui:Action=\"submit\" Content=\"Submit\"/>";
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
    private static UiPublish Source(string body, object? state = null) => new("form", 0, 1,
        "<StackPanel " + Ns + ">" + body + "</StackPanel>", J(state ?? new { value = "" }),
        Actions: [new("submit", "copy", Text: "Submitted")]);
    private static UiElement Button(UiSnapshot snapshot) => UiSessionStore.Flatten(snapshot.Roots).Single(n => n.Key == "/submit");

    [Theory]
    [InlineData("TextBox", "", "\"   \"", "\"ready\"")]
    [InlineData("CheckBox", "", "false", "true")]
    [InlineData("NumericUpDown", "", "null", "0")]
    [InlineData("CalendarDatePicker", "", "null", "\"2026-10-09\"")]
    [InlineData("ComboBox", "ItemsSource=\"[&quot;A&quot;]\"", "-1", "0")]
    public void Required_values_use_input_type_semantics(string type, string attributes, string empty, string filled)
    {
        var body = $"<ui:Form><ui:Field IsRequired=\"True\"><{type} ui:Bind=\"value\" {attributes}/></ui:Field>{Submit}</ui:Form>";
        var request = Source(body, new { value = JsonSerializer.Deserialize<JsonElement>(empty) });
        var store = new UiSessionStore(); var snapshot = store.Publish(request, "owner");
        Assert.False(Button(snapshot).Properties["IsEnabled"].GetBoolean());
        snapshot = store.ChangeState(new(snapshot.Id, snapshot.Revision, snapshot.StateRevision, "value", JsonSerializer.Deserialize<JsonElement>(filled)), "owner");
        Assert.True(Button(snapshot).Properties["IsEnabled"].GetBoolean());
    }

    [Theory]
    [InlineData("<StackPanel IsVisible=\"False\">", "</StackPanel>", true)]
    [InlineData("<StackPanel IsEnabled=\"False\">", "</StackPanel>", true)]
    [InlineData("<Expander IsExpanded=\"False\">", "</Expander>", true)]
    [InlineData("<Expander IsExpanded=\"True\">", "</Expander>", false)]
    [InlineData("<TabControl SelectedIndex=\"1\"><TabItem>", "</TabItem><TabItem><TextBlock Text=\"Active\"/></TabItem></TabControl>", true)]
    [InlineData("<TabControl SelectedIndex=\"0\"><TabItem>", "</TabItem><TabItem><TextBlock Text=\"Inactive\"/></TabItem></TabControl>", false)]
    public void Only_active_enabled_fields_participate(string start, string end, bool valid)
    {
        var snapshot = new UiSessionStore().Publish(Source("<ui:Form>" + start + Field + end + Submit + "</ui:Form>"), "owner");
        Assert.Equal(valid, Button(snapshot).Properties["IsEnabled"].GetBoolean());
    }

    [Fact]
    public void Hiding_error_messages_does_not_enable_submission()
    {
        var request = Source("<ui:Form ShowErrors=\"False\">" + Field + "<ui:ValidationSummary/>" + Submit + "</ui:Form>");
        var snapshot = new UiSessionStore().Publish(request, "owner");
        Assert.False(Button(snapshot).Properties["IsEnabled"].GetBoolean());
        Assert.DoesNotContain("NEEDS_A_VALUE", snapshot.FallbackMarkdown);
    }

    [Theory]
    [InlineData("<ui:SubmitButton/>")]
    [InlineData("<ui:ValidationSummary/>")]
    [InlineData("<ui:Form><ui:Form/></ui:Form>")]
    [InlineData("<ui:Field/>")]
    [InlineData("<ui:Field><TextBlock Text=\"not an input\"/></ui:Field>")]
    public void Invalid_structures_are_rejected_atomically(string source)
    {
        var store = new UiSessionStore();
        Assert.Equal("invalid_form", Assert.Throws<UiException>(() => store.Publish(Source(source), "owner")).Code);
        Assert.Empty(store.List("owner"));
    }

    [Fact]
    public void Repeated_forms_keep_validation_and_item_scopes_separate()
    {
        var request = Source("""
            <ui:Form ui:Key="row" ui:Each="{ui:Expr data.rows}" ui:ItemKey="{ui:Expr item.id}" IsValid="{ui:Expr item.valid}">
              <ui:SubmitButton ui:Key="submit" ui:Action="submit" Content="{ui:Expr item.id}"/>
            </ui:Form>
            """) with
        {
            Data = J(new { rows = new[] { new { id = "invalid", valid = false }, new { id = "valid", valid = true } } }),
            Actions = [new("submit", "copy", Text: "{ui:Expr item.id}")]
        };
        var store = new UiSessionStore(); var snapshot = store.Publish(request, "owner");
        var buttons = UiSessionStore.Flatten(snapshot.Roots).Where(n => n.ActionId == "submit").ToArray();
        Assert.False(buttons[0].Properties["IsEnabled"].GetBoolean());
        Assert.True(buttons[1].Properties["IsEnabled"].GetBoolean());
        Assert.Equal("valid", store.PrepareAction(new(snapshot.Id, snapshot.Revision, 0, buttons[1].Key), "owner").Text);
        Assert.Equal("invalid_action", Assert.Throws<UiException>(() => store.PrepareAction(new(snapshot.Id, snapshot.Revision, 0, buttons[0].Key), "owner")).Code);
    }
}
