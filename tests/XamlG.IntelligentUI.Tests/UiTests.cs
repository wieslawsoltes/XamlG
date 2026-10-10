using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Simple;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(XamlG.IntelligentUI.Tests.TestApplication))]
namespace XamlG.IntelligentUI.Tests;

public sealed class TestApplication : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
public sealed class UiTests
{
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
    private const string Ns = "xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\"";
    private static string Wrap(string children) => $"<StackPanel {Ns}>{children}</StackPanel>";
    private static UiPublish Request(string? source = null) => new("price", 0, 1, source ?? Wrap("<Slider ui:Key=\"seats\" ui:Bind=\"seats\" Minimum=\"1\" Maximum=\"50\"/><TextBlock ui:Key=\"price\" Text=\"{ui:Expr state.seats * data.unit}\"/>"), J(new { seats = 8 }), J(new { unit = 29 }));
    // String output is explicit C#: numbers do not silently become text in typed properties.
    private static UiPublish Price() => Request(Wrap("<Slider ui:Key=\"seats\" ui:Bind=\"seats\" Minimum=\"1\" Maximum=\"50\"/><TextBlock ui:Key=\"price\" Text=\"{ui:Expr &quot;$&quot; + state.seats * data.unit}\"/>"));

    [Fact] public void ComputesPriceAndFallback()
    {
        var store = new UiSessionStore(); var result = store.Publish(Price(), "owner");
        Assert.Equal("$232", result.Roots[0].Children[1].Properties["Text"].GetString()); Assert.Contains("$232", result.FallbackMarkdown);
        var next = store.ChangeState(new("price", result.Revision, result.StateRevision, "seats", J(9)), "owner");
        Assert.Equal("$261", next.Roots[0].Children[1].Properties["Text"].GetString()); Assert.Equal(1, next.StateRevision);
    }
    [Fact] public void KeepsInteractiveStateAcrossStructuralStreamUpdates()
    {
        var store = new UiSessionStore(); var first = store.Publish(Price() with { IsFinal = false }, "owner");
        store.ChangeState(new("price", 1, 0, "seats", J(12)), "owner");
        var next = store.Publish(Price() with { ExpectedRevision = 1, Sequence = 2 }, "owner");
        Assert.Equal(12, next.State.GetProperty("seats").GetInt32()); Assert.Contains("$348", next.FallbackMarkdown);
    }
    [Fact] public void RejectsStaleSourceStateAndData()
    {
        var store = new UiSessionStore(); store.Publish(Price(), "owner");
        store.ChangeState(new("price", 1, 0, "seats", J(10)), "owner");
        Assert.Throws<UiException>(() => store.ChangeState(new("price", 1, 0, "seats", J(11)), "owner"));
        Assert.Throws<UiException>(() => store.Publish(Price(), "owner"));
        Assert.Throws<UiException>(() => store.ChangeData(new("price", 0, J(new { unit = 99 })), "owner"));
    }
    [Fact] public void RejectsSkippedStreamSequence()
    {
        var store = new UiSessionStore(); store.Publish(Price(), "owner");
        Assert.Throws<UiException>(() => store.Publish(Price() with { ExpectedRevision = 1, Sequence = 3 }, "owner"));
    }
    [Fact] public void OwnershipIsEnforced()
    {
        var store = new UiSessionStore(); store.Publish(Price(), "owner");
        Assert.Throws<UiException>(() => store.Read("price", "other"));
        Assert.Throws<UiException>(() => store.Release(new("price", 1), "other"));
        Assert.Throws<UiException>(() => store.Publish(Price() with { ExpectedRevision = 1, Sequence = 2 }, "other"));
    }
    [Fact] public void BadExpressionLeavesPreviousViewIntact()
    {
        var store = new UiSessionStore(); var original = store.Publish(Price(), "owner");
        Assert.Throws<UiException>(() => store.Publish(Price() with { ExpectedRevision = 1, Sequence = 2, Xaml = Wrap("<TextBlock Text=\"{ui:Expr 1 / 0}\"/>") }, "owner"));
        Assert.Same(original, store.Read("price", "owner"));
    }
    [Theory]
    [InlineData("System.IO.File.ReadAllText(\"secret\")")]
    [InlineData("new object()")]
    [InlineData("state.n = 2")]
    [InlineData("state.n++")]
    [InlineData("typeof(string)")]
    [InlineData("((System.Func<int>)(() => 1))()")]
    [InlineData("true ? 1 : System.Environment.Exit(0)")]
    public void RejectsExecutableCSharpEvenInDeadBranches(string expression) => Assert.Throws<UiException>(() => UiExpression.Parse(expression));
    [Fact] public void HandlesShortCircuitAndInterpolation()
    {
        var result = UiExpression.Parse("$\"Total: {(state.enabled ? data.price * 2 : 0)}\"").Evaluate(J(new { enabled = true }), J(new { price = 7 }));
        Assert.Equal("Total: 14", result.GetString());
        Assert.False(UiExpression.Parse("false && data.noSuchField.bad").Evaluate(J(new { }), J(new { })).GetBoolean());
    }
    [Theory]
    [InlineData("<Button Click=\"DeleteAll\"/>")]
    [InlineData("<TextBlock Text=\"{Binding Password, Converter=Unregistered}\"/>")]
    [InlineData("<Unknown/>")]
    [InlineData("<Button Width=\"-1\"/>")]
    [InlineData("<TextBlock xmlns:evil=\"clr-namespace:Exploit\"/>")]
    public void RejectsUnregisteredMarkup(string source) => Assert.False(new UiCompiler().Compile(Wrap(source)).Success);
    [Fact] public void RejectsDtd()
    {
        var result = new UiCompiler().Compile("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///etc/passwd'>]>" + Wrap("<TextBlock>&x;</TextBlock>")); Assert.False(result.Success);
    }
    [Fact] public void RepairsOnlyNonfinalPrefix()
    {
        var text = $"<StackPanel {Ns}><TextBlock Text=\"Ready\"/><Button Content=\"";
        var compiler = new UiCompiler(); var partial = compiler.Compile(text, false);
        Assert.True(partial.Success); Assert.False(partial.IsComplete); Assert.NotEmpty(partial.Diagnostics);
        Assert.False(compiler.Compile(text, true).Success);
    }
    [Fact] public void RepeatsUseStableItemKeysAndIncludeComputedFallback()
    {
        var store = new UiSessionStore();
        var source = Wrap("<TextBlock ui:Key=\"row\" ui:Each=\"{ui:Expr data.rows}\" ui:ItemKey=\"{ui:Expr item.id}\" Text=\"{ui:Expr item.name}\"/>");
        var request = Request(source) with { Data = J(new { rows = new[] { new { id = "a", name = "Alpha" }, new { id = "b", name = "Beta" } } }) };
        var snapshot = store.Publish(request, "owner");
        Assert.Equal(2, snapshot.Roots[0].Children.Length); Assert.Contains("Alpha", snapshot.FallbackMarkdown);
        Assert.NotEqual(snapshot.Roots[0].Children[0].Key, snapshot.Roots[0].Children[1].Key);
        Assert.Throws<UiException>(() => store.ChangeData(new("price", 1, J(new { rows = new[] { new { id = "a", name = "A" }, new { id = "a", name = "B" } } })), "owner"));
    }
    [Fact] public void NoArbitraryStateMutationOrOutOfRangeValue()
    {
        var store = new UiSessionStore(); store.Publish(Price(), "owner");
        Assert.Throws<UiException>(() => store.ChangeState(new("price", 1, 0, "seats", J(100)), "owner"));
        Assert.Throws<UiException>(() => store.ChangeState(new("price", 1, 0, "secret", J("value")), "owner"));
        Assert.Equal(8, store.Read("price", "owner").State.GetProperty("seats").GetInt32());
    }
    [Fact] public void ActionsReturnIntentWithoutExecutingTools()
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(Request(Wrap("<Button ui:Key=\"run\" ui:Action=\"run\" Content=\"Build\"/>")) with
        { Actions = [new("run", "tool", Tool: "xamlg_compiler_compile", Arguments: J(new { expectedRevision = "{ui:Expr data.revision}" }))], Data = J(new { revision = 3 }) }, "owner");
        var intent = store.PrepareAction(new("price", 1, 0, "/run"), "owner");
        Assert.Equal("xamlg_compiler_compile", intent.Tool); Assert.Equal(3, intent.Arguments!.Value.GetProperty("expectedRevision").GetInt32());
    }
    [Fact] public void RejectsDisabledActionsAndScriptUrls()
    {
        var store = new UiSessionStore();
        store.Publish(Request(Wrap("<Button ui:Key=\"link\" ui:Action=\"link\"/>")) with { Actions = [new("link", "openUrl", "javascript:alert(1)")] }, "owner");
        Assert.Throws<UiException>(() => store.PrepareAction(new("price", 1, 0, "/link"), "owner"));
        store.Publish(Request($"<StackPanel {Ns} IsEnabled=\"False\"><Button ui:Key=\"link\" ui:Action=\"link\"/></StackPanel>") with { ExpectedRevision = 1, Sequence = 2, Actions = [new("link", "copy", "hello")] }, "owner");
        Assert.Throws<UiException>(() => store.PrepareAction(new("price", 2, 0, "/link"), "owner"));
    }
    [Fact] public void SnapshotDoesNotRetainDisposedJsonDocument()
    {
        var store = new UiSessionStore();
        using (var document = JsonDocument.Parse("{\"seats\":8}")) store.Publish(Price() with { InitialState = document.RootElement }, "owner");
        Assert.Equal(8, store.Read("price", "owner").State.GetProperty("seats").GetInt32());
    }
    [Fact] public void LimitsAndClearAreBounded()
    {
        var store = new UiSessionStore(new(limits: new(Surfaces: 1))); store.Publish(Price(), "owner");
        Assert.Throws<UiException>(() => store.Publish(Price() with { Id = "other" }, "owner"));
        store.Clear(); Assert.Null(store.ReadLocal("price")); store.Publish(Price(), "owner");
    }
    [Fact] public void RejectsDuplicateJsonProperties()
    {
        using var document = JsonDocument.Parse("{\"seats\":8,\"seats\":9}");
        Assert.Throws<UiException>(() => new UiSessionStore().Publish(Price() with { InitialState = document.RootElement }, "owner"));
    }
    [AvaloniaFact] public void NativeUpdatesRetainControlsAndResetRemovedProperties()
    {
        var store = new UiSessionStore(); var snapshot = store.Publish(Price(), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var slider = Assert.IsType<Slider>(renderer.Find("/seats")); var text = Assert.IsType<TextBlock>(renderer.Find("/price"));
        renderer.StateChanged += change => renderer.Apply(store.ChangeState(change, "owner"));
        slider.Value = 9;
        Assert.Same(slider, renderer.Find("/seats")); Assert.Equal("$261", text.Text);
        var next = store.Publish(Price() with { ExpectedRevision = 1, Sequence = 2 }, "owner"); renderer.Apply(next);
        Assert.Equal(9, slider.Value); Assert.Same(text, renderer.Find("/price"));
    }
    [AvaloniaFact] public void NativeClickPreparesCurrentAction()
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(Request(Wrap("<Button ui:Key=\"copy\" ui:Action=\"copy\">Copy</Button>")) with { Actions = [new("copy", "copy", "Hello")] }, "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        UiActionIntent? intent = null; renderer.ActionRequested += request => intent = store.PrepareAction(request, "owner");
        Assert.IsType<Button>(renderer.Find("/copy")).RaiseEvent(new(global::Avalonia.Controls.Button.ClickEvent));
        Assert.Equal("Hello", intent!.Text);
    }
    [AvaloniaFact] public void ReplacingContainerRetainsKeyedChildren()
    {
        var store = new UiSessionStore();
        var first = store.Publish(Request($"<StackPanel {Ns} ui:Key=\"parent\"><TextBox ui:Key=\"input\" Text=\"before\"/></StackPanel>"), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(first);
        var input = Assert.IsType<TextBox>(renderer.Find("/input"));
        var next = store.Publish(Request($"<Border {Ns} ui:Key=\"parent\"><TextBox ui:Key=\"input\" Text=\"after\"/></Border>") with { ExpectedRevision = 1, Sequence = 2 }, "owner");
        renderer.Apply(next);
        Assert.Same(input, renderer.Find("/input")); Assert.Equal("after", input.Text);
        Assert.Same(renderer.Find("/parent"), input.Parent);
    }
    [AvaloniaFact] public void RejectedInputCanRestoreCommittedValue()
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(Request(Wrap("<TextBox ui:Key=\"input\" ui:Bind=\"text\"/>")) with { InitialState = J(new { text = "before" }) }, "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var input = Assert.IsType<TextBox>(renderer.Find("/input"));
        input.Text = "uncommitted"; renderer.Apply(snapshot);
        Assert.Equal("before", input.Text);
    }

}
