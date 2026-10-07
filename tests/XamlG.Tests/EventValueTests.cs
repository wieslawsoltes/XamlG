using XamlG.Compiler;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class EventValueTests
{
    private const string Model = """
        using System;
        using XamlG.Runtime;
        namespace Demo;
        public class View
        {
            public int Calls { get; set; }
            public int Providers { get; set; }
            public event EventHandler Click;
            public void Handle(object sender, EventArgs args) => Calls++;
            public void Raise() => Click?.Invoke(this, EventArgs.Empty);
        }
        public partial class Component : View { }
        public static class Attached
        {
            public static void AddClickHandler(View view, EventHandler handler) => view.Click += handler;
            public static void RemoveClickHandler(View view, EventHandler handler) => view.Click -= handler;
        }
        public class HandlerExtension
        {
            public EventHandler ProvideTypedValue(IServiceProvider services)
            {
                var root = (View)((IXamlRootObjectProvider)services.GetService(typeof(IXamlRootObjectProvider))).RootObject;
                var target = (IXamlProvideValueTarget)services.GetService(typeof(IXamlProvideValueTarget));
                if (!ReferenceEquals(root, target.TargetObject)) throw new InvalidOperationException("Missing event target");
                root.Providers++;
                return root.Handle;
            }
        }
        """;

    [Theory]
    [InlineData("", "<View.Click>Handle</View.Click>", 0)]
    [InlineData("", "<View.Click><x:String>Handle</x:String></View.Click>", 0)]
    [InlineData("Click='{Handler}'", "", 1)]
    [InlineData("", "<View.Click><HandlerExtension/></View.Click>", 1)]
    [InlineData("Attached.Click='{Handler}'", "", 1)]
    [InlineData("", "<Attached.Click><HandlerExtension/></Attached.Click>", 1)]
    public void EventValuesAreEvaluatedOnceAndUnsubscribedWithTheSession(string attributes, string content, int providers)
    {
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Demo' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " + attributes + ">" + content + "</View>", Model);
        var root = code.Build();
        var raise = root.GetType().GetMethod("Raise")!;
        raise.Invoke(root, null);
        Assert.Equal(1, root.GetType().GetProperty("Calls")!.GetValue(root));
        Assert.Equal(providers, root.GetType().GetProperty("Providers")!.GetValue(root));
        Assert.True(XamlRuntimeSession.TryGet(root, out var session));
        session!.Dispose();
        raise.Invoke(root, null);
        Assert.Equal(1, root.GetType().GetProperty("Calls")!.GetValue(root));
        Assert.Equal(providers, root.GetType().GetProperty("Providers")!.GetValue(root));
    }

    [Fact]
    public void EventValueObjectsParticipateInNamedFieldGeneration()
    {
        using var code = CompiledXaml.Create("""
            <View xmlns="clr-namespace:Demo" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="Demo.Component">
              <View.Click><HandlerExtension x:Name="handler" x:FieldModifier="public"/></View.Click>
            </View>
            """, Model);
        var root = code.Build();
        Assert.NotNull(root.GetType().GetField("handler")!.GetValue(root));
    }

    [Fact]
    public void MultipleValuesInOneEventAssignmentAreDiagnosed()
    {
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse("""
            <View xmlns="clr-namespace:Demo" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <View.Click><x:String>Handle</x:String><x:String>Handle</x:String></View.Click>
            </View>
            """), CompilationFactory.Create(Model));
        Assert.False(document.Success);
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == "XG1017");
    }
}
