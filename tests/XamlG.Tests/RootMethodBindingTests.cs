using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class RootMethodBindingTests
{
    private const string Model = """
        using System;
        using XamlG.Runtime;
        namespace Demo;
        public class View
        {
            public Action Callback { get; set; }
            public Func<string, object> Convert { get; set; }
            public Action<int> Number { get; set; }
            public Action<string> Ambiguous { get; set; }
            public Runner Child { get; set; }
            [DeferredContent] public Func<IServiceProvider, object> Template { get; set; }
            public event EventHandler Click;
            public int Calls { get; set; }
            public void Handle() => Calls++;
            private void Hidden() { }
            public void Widen(long value) { }
            public void RefValue(ref int value) { }
            public string Parse(object value) => "object";
            public string Parse(string value) => "string";
            public void Choose(IComparable value) { }
            public void Choose(ICloneable value) { }
            public void HandleClick(object sender, EventArgs args) => Calls++;
            public void Raise() => Click?.Invoke(this, EventArgs.Empty);
        }
        public class Runner(Action callback) { public Action Callback { get; } = callback; }
        public partial class Component : View
        {
            private void PrivateHandle() => Calls++;
        }
        """;

    [Theory]
    [InlineData("<View xmlns='clr-namespace:Demo' Callback='Handle'/>")]
    [InlineData("<View xmlns='clr-namespace:Demo'><View.Callback>Handle</View.Callback></View>")]
    [InlineData("<View xmlns='clr-namespace:Demo' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><View.Callback><x:String>Handle</x:String></View.Callback></View>")]
    [InlineData("<View xmlns='clr-namespace:Demo' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Class='Demo.Component' Callback='PrivateHandle'/>")]
    public void BindsDelegatePropertiesToTheRootInstance(string xaml)
    {
        using var code = CompiledXaml.Create(xaml, Model);
        var root = code.Build();
        ((Action)root.GetType().GetProperty("Callback")!.GetValue(root)!)();
        Assert.Equal(1, root.GetType().GetProperty("Calls")!.GetValue(root));
    }

    [Fact]
    public void BindsNestedConstructorAndDeferredDelegatesToTheirOwner()
    {
        using var code = CompiledXaml.Create("""
            <View xmlns="clr-namespace:Demo" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <View.Child><Runner><x:Arguments><x:String>Handle</x:String></x:Arguments></Runner></View.Child>
              <View.Template><Runner><x:Arguments><x:String>Handle</x:String></x:Arguments></Runner></View.Template>
            </View>
            """, Model);
        var root = code.Build();
        var child = root.GetType().GetProperty("Child")!.GetValue(root)!;
        ((Action)child.GetType().GetProperty("Callback")!.GetValue(child)!)();
        var factory = (Func<IServiceProvider, object>)root.GetType().GetProperty("Template")!.GetValue(root)!;
        var deferred = factory(null!);
        ((Action)deferred.GetType().GetProperty("Callback")!.GetValue(deferred)!)();
        Assert.Equal(2, root.GetType().GetProperty("Calls")!.GetValue(root));
    }

    [Fact]
    public void SelectsTheMostSpecificCompatibleOverload()
    {
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Demo' Convert='Parse'/>", Model);
        var root = code.Build();
        var callback = (Func<string, object>)root.GetType().GetProperty("Convert")!.GetValue(root)!;
        Assert.Equal("string", callback("input"));
    }

    [Fact]
    public void BindsEventsOnRootsWithoutCodeBehind()
    {
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Demo' Click='HandleClick'/>", Model);
        var root = code.Build();
        root.GetType().GetMethod("Raise")!.Invoke(root, null);
        Assert.Equal(1, root.GetType().GetProperty("Calls")!.GetValue(root));
    }

    [Theory]
    [InlineData("Callback='Hidden'")]
    [InlineData("Callback='Missing'")]
    [InlineData("Number='Widen'")]
    [InlineData("Number='RefValue'")]
    [InlineData("Ambiguous='Choose'")]
    public void DiagnosesInaccessibleIncompatibleAndAmbiguousMethods(string assignment)
    {
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<View xmlns='clr-namespace:Demo' " + assignment + "/>"), CompilationFactory.Create(Model));
        Assert.False(document.Success);
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == "XG1008");
    }
}
