using System.Text.Json;
using Microsoft.CodeAnalysis;
using XamlG.Automation;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiCSharpWorkerSessionTests
{
    private static readonly Lazy<MetadataReference[]> References = new(() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? throw new InvalidOperationException("Missing runtime reference inventory."))
        .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray());
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, AutomationJson.Options);
    private static UiPublish Request() => new("worker", 0, 1,
        """
        <StackPanel xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui">
          <TextBlock Text="{ui:Expr state.GetProperty(&quot;n&quot;).GetDecimal().ToString()}"/>
          <Button ui:Key="next" ui:Action="next" Content="Next"/>
        </StackPanel>
        """, JsonSerializer.SerializeToElement(new { n = 1 }), Actions:
        [new("next", "state", Arguments: JsonSerializer.SerializeToElement(new { n = "{ui:Expr state.GetProperty(\"n\").GetDecimal() + 1}" }))]);

    [Fact]
    public void Worker_protocol_runs_approved_full_CSharp_and_preserves_revision_checks()
    {
        var approvals = 0;
        using var worker = new UiCSharpWorkerSession(References.Value, _ => { approvals++; return true; });
        var snapshot = JsonSerializer.Deserialize<UiSnapshot>(worker.Dispatch("publish", Json(Request())), AutomationJson.Options)!;
        Assert.True(approvals > 0); Assert.Contains("1", snapshot.FallbackMarkdown);
        var call = new UiActionCall(snapshot.Id, snapshot.Revision, snapshot.StateRevision, "/next");
        var next = JsonSerializer.Deserialize<UiSnapshot>(worker.Dispatch("state_action", Json(call)), AutomationJson.Options)!;
        Assert.Equal(2, next.State.GetProperty("n").GetDecimal());
        Assert.Equal("revision_conflict", Assert.Throws<UiException>(() => worker.Dispatch("state_action", Json(call))).Code);
        Assert.Equal("execution_used", Assert.Throws<UiException>(() => worker.Dispatch("publish", Json(Request()))).Code);
    }

    [Fact]
    public void Worker_dispatch_cannot_grant_tools_network_or_application_authority()
    {
        using var worker = new UiCSharpWorkerSession(References.Value, _ => true);
        foreach (var method in new[] { "tool", "action", "openUrl", "register", "form_validate_start", "restore", "compile_anything" })
            Assert.Equal("execution_command", Assert.Throws<UiException>(() => worker.Dispatch(method, "{}")).Code);
        var request = Request() with { Actions = [new("next", "message", Text: "External")] };
        Assert.Throws<UiException>(() => worker.Dispatch("publish", Json(request)));
    }

    [Fact]
    public void Worker_refusal_and_request_limits_are_not_silently_bypassed()
    {
        using var worker = new UiCSharpWorkerSession(References.Value, _ => false);
        Assert.Throws<UiException>(() => worker.Dispatch("publish", Json(Request())));
        Assert.Equal("execution_used", Assert.Throws<UiException>(() => worker.Dispatch("publish", Json(Request()))).Code);
        Assert.Equal("execution_limit", Assert.Throws<UiException>(() => worker.Dispatch("read", new string('x', 1048577))).Code);
        worker.Dispose();
        Assert.Throws<ObjectDisposedException>(() => worker.Dispatch("read", "{}"));
    }
}
