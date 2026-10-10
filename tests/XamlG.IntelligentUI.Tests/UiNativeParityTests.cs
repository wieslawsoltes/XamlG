using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiNativeParityTests
{
    [AvaloniaFact]
    public void Native_action_evaluates_its_patch_exactly_once()
    {
        var expressions = new CountingExpressions();
        var store = new UiSessionStore(new UiCompiler(expressionCompiler: expressions));
        var initial = store.Publish(UiInteractionExamples.Counter(), "owner");
        using var session = new UiAvaloniaSession(store, UiPresentation.From(initial), "owner");
        var button = session.View.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Increment"));
        var call = new UiActionCall(initial.Id, initial.Revision, initial.StateRevision, "/increment");
        Assert.True(UiActionRouting.IsStateAction(initial, call));
        Assert.Equal(0, expressions.ActionEvaluations);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, expressions.ActionEvaluations);
        Assert.Equal(1, session.Snapshot!.State.GetProperty("count").GetDecimal());
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, expressions.ActionEvaluations);
        Assert.Equal(2, session.Snapshot!.State.GetProperty("count").GetDecimal());
        Assert.Equal("revision_conflict", Assert.Throws<UiException>(() => UiActionRouting.IsStateAction(session.Snapshot!, call)).Code);
    }

    [AvaloniaFact]
    public void Viewbox_child_is_reconciled_reparented_and_removed_without_recreation()
    {
        const string child = "<Button ui:Key=\"child\" Content=\"Stable\"/>";
        static string Xaml(string left, string right) => "<StackPanel xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\"><Viewbox ui:Key=\"left\">" + left + "</Viewbox><Viewbox ui:Key=\"right\">" + right + "</Viewbox></StackPanel>";
        var store = new UiSessionStore();
        var snapshot = store.Publish(new("boxes", 0, 1, Xaml(child, "")), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var button = Assert.IsType<Button>(renderer.Find("/child"));
        var left = Assert.IsType<Viewbox>(renderer.Find("/left"));
        var right = Assert.IsType<Viewbox>(renderer.Find("/right"));
        Assert.Same(button, left.Child); Assert.Null(right.Child);
        snapshot = store.Publish(new("boxes", snapshot.Revision, 2, Xaml("", child)), "owner"); renderer.Apply(snapshot);
        Assert.Same(button, renderer.Find("/child")); Assert.Null(left.Child); Assert.Same(button, right.Child);
        snapshot = store.Publish(new("boxes", snapshot.Revision, 3, Xaml("", "")), "owner"); renderer.Apply(snapshot);
        Assert.Null(right.Child); Assert.Null(renderer.Find("/child"));
    }

    private sealed class CountingExpressions : IUiExpressionCompiler
    {
        public string Language => "counted-test-expressions";
        public int ActionEvaluations { get; private set; }
        public IUiExpression Compile(string source, UiLimits limits) => new Counted(this, UiExpression.Parse(source, limits));
        private sealed class Counted(CountingExpressions owner, IUiExpression inner) : IUiExpression
        {
            public string Source => inner.Source;
            public JsonElement Evaluate(JsonElement state, JsonElement data, JsonElement? item = null)
            {
                if (Source == "state.count + 1") owner.ActionEvaluations++;
                return inner.Evaluate(state, data, item);
            }
        }
    }
}
