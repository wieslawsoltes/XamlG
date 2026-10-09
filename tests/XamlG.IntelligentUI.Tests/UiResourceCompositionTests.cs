using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiResourceCompositionTests
{
    [Fact]
    public void Shipping_resource_contains_every_trusted_module_without_external_script_requests()
    {
        var html = UiResourceHtml.Html;
        Assert.DoesNotContain("@include", html);
        Assert.DoesNotContain("<script src=", html);
        Assert.Contains("function validSnapshot", html);
        Assert.Contains("function create(node)", html);
        Assert.Contains("function changeState", html);
        Assert.Contains("async function submitForm", html);
        Assert.Contains("function onMessage", html);
        Assert.Contains("connect-src 'none'", html);
        Assert.Same(html, UiResourceHtml.Html);
    }

    [Fact]
    public async Task Retained_data_binding_preserves_form_interaction_history()
    {
        var store = new UiSessionStore(); var initial = store.Publish(UiFormExamples.Configuration(), "owner");
        var original = Assert.Single(store.ReadForms(initial.Id, "owner"));
        var input = original.Fields.First(field => field.StateKey == "project");
        var touched = store.TouchForm(new(initial.Id, initial.Revision, initial.StateRevision, original.Id, input.Key), "owner");
        using var document = System.Text.Json.JsonDocument.Parse("20");
        var data = new UiDataStore(); var handle = data.Put(document.RootElement, "owner");
        await store.BindDataAsync(new(touched.Id, touched.Revision, [new("limit", new(handle.Id, handle.Version))]), "owner", data, TestContext.Current.CancellationToken);
        var restored = Assert.Single(store.ReadForms(initial.Id, "owner"));
        Assert.True(restored.Fields.Single(field => field.Key == input.Key).Touched);
        Assert.False(restored.IsDirty);
        Assert.Equal(input.InitialValue.GetRawText(), restored.Fields.Single(field => field.Key == input.Key).InitialValue.GetRawText());
    }
}
