using System.Collections;
using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.Runtime;
using Xunit;
namespace XamlG.Tests;

public sealed class EmissionTests
{
    internal const string Model = """
        using System; using System.Collections.Generic; using System.ComponentModel; using XamlG.Runtime;
        namespace Fixture {
          public class Panel { [Content] public List<object> Children {get;} = new(); public string Title {get;set;} public object Value {get;set;} public int Count {get;set;} public Type Type {get;set;} }
          public class Item { public string Name {get;set;} public string Text {get;set;} public int Number {get;set;} public int Initial {get;init;} }
          public static class Attached { public static int Last; public static int GetRow(Item target) => Last; public static void SetRow(Item target, int value) => Last=value; }
          public class EchoExtension { public EchoExtension(string value) { Value=value; } public string Value {get;set;} public string ProvideValue(IServiceProvider provider) => Value; }
          public class RootExtension { public object ProvideValue(IServiceProvider provider) => ((IXamlRootObjectProvider)provider.GetService(typeof(IXamlRootObjectProvider))).RootObject; }
          public class TargetExtension { public object ProvideValue(IServiceProvider provider) => ((IXamlProvideValueTarget)provider.GetService(typeof(IXamlProvideValueTarget))).TargetObject; }
          public class Template { [Content, DeferredContent] public Func<IServiceProvider,object> Content {get;set;} }
          public class Button : Item { public event EventHandler Click; public void Raise() => Click?.Invoke(this, EventArgs.Empty); }
          public partial class Window : Panel { public Window() { } private void OnClick(object sender, EventArgs e) { Count++; } }
          [TypeConverter(typeof(SizeConverter))] public class Size { public int Value {get;set;} }
          public class SizeConverter : TypeConverter { public override object ConvertFrom(ITypeDescriptorContext context, System.Globalization.CultureInfo culture, object value) => new Size { Value=int.Parse((string)value, culture) }; }
          public class Sized { public Size Size {get;set;} }
          public class Factory { private Factory(int value) { Value=value; } public int Value {get;} public static Factory Create(int value) => new Factory(value); }
        }
        """;
    private const string Namespaces = "xmlns='clr-namespace:Fixture' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
    private static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);
    [Fact]
    public void BuildsTypedGraphAndAttachedProperty()
    {
        using var code = CompiledXaml.Create("<Panel " + Namespaces + " Title='{Echo hello}' Count='42'><Item Text='one' Attached.Row='3'/><Item Text='two'/></Panel>", Model);
        var root = code.Build(); Assert.Equal("hello", Property(root, "Title")); Assert.Equal(42, Property(root, "Count"));
        var children = (IList)Property(root, "Children")!; Assert.Equal(2, children.Count); Assert.Equal("one", Property(children[0]!, "Text"));
        Assert.Equal(3, code.Assembly.GetType("Fixture.Attached")!.GetField("Last")!.GetValue(null)); Assert.NotEmpty(code.Emission.SourceMappings);
    }
    [Fact]
    public void CompilesLiteralsWithoutCodeInjection()
    {
        using var code = CompiledXaml.Create("<Panel " + Namespaces + " Title='{}&quot;;throw new Exception(); // &#10;{literal}'/>", Model);
        Assert.Equal("\";throw new Exception(); // \n{literal}", Property(code.Build(), "Title"));
    }
    [Fact]
    public void CompiledCodeBehindInitializesNamedFieldsAndPrivateEvents()
    {
        using var code = CompiledXaml.Create("<Panel " + Namespaces + " x:Class='Fixture.Window'><Button x:Name='button' Click='OnClick'/></Panel>", Model);
        var root = Activator.CreateInstance(code.Assembly.GetType("Fixture.Window")!)!; root.GetType().GetMethod("InitializeComponent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(root, null); var children = (IList)Property(root, "Children")!;
        var field = root.GetType().GetField("button", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Same(children[0], field.GetValue(root)); children[0]!.GetType().GetMethod("Raise")!.Invoke(children[0], null); Assert.Equal(1, Property(root, "Count"));
        Assert.True(XamlRuntimeSession.TryGet(root, out var session)); session!.Dispose();
        children[0]!.GetType().GetMethod("Raise")!.Invoke(children[0], null); Assert.Equal(1, Property(root, "Count"));
    }
    [Fact]
    public void ForwardReferencesResolveAfterAllNamesAreRegistered()
    {
        using var code = CompiledXaml.Create("<Panel " + Namespaces + " Value='{x:Reference later}'><Item x:Name='later' Text='named'/></Panel>", Model);
        var root = code.Build(); Assert.Same(((IList)Property(root, "Children")!)[0], Property(root, "Value"));
    }
    [Fact]
    public void MarkupServicesSeeTheRootAndTheAssignmentTarget()
    {
        using var code = CompiledXaml.Create("<Panel " + Namespaces + " Value='{Root}'><Panel Value='{Target}'/></Panel>", Model);
        var root = code.Build(); Assert.Same(root, Property(root, "Value"));
        var child = ((IList)Property(root, "Children")!)[0]!; Assert.Same(child, Property(child, "Value"));
    }
    [Fact]
    public void DeferredContentBuildsFreshInstancesWithCapturedRoot()
    {
        using var code = CompiledXaml.Create("<Panel " + Namespaces + "><Template><Panel Value='{Root}'><Item x:Name='inside'/></Panel></Template></Panel>", Model);
        var root = code.Build(); var template = ((IList)Property(root, "Children")!)[0]!; var factory = (Delegate)Property(template, "Content")!;
        var first = factory.DynamicInvoke(new object?[] { null })!; var second = factory.DynamicInvoke(new object?[] { null })!;
        Assert.NotSame(first, second); Assert.Same(root, Property(first, "Value")); Assert.Same(root, Property(second, "Value"));
    }
    [Fact]
    public void InitOnlySettersWorkOnExistingAndNewObjects()
    {
        using var code = CompiledXaml.Create("<Item " + Namespaces + " Initial='17'/>", Model); Assert.Equal(17, Property(code.Build(), "Initial"));
    }
    [Fact]
    public void ConverterRunsInGeneratedCodeNotInTheCompiler()
    {
        using var code = CompiledXaml.Create("<Sized " + Namespaces + " Size='37'/>", Model); Assert.Equal(37, Property(Property(code.Build(), "Size")!, "Value"));
    }
    [Fact]
    public void UsesFactoryMethodsAndTypedArguments()
    {
        using var code = CompiledXaml.Create("<Factory " + Namespaces + " x:FactoryMethod='Create'><x:Arguments><x:Int32>31</x:Int32></x:Arguments></Factory>", Model); Assert.Equal(31, Property(code.Build(), "Value"));
    }
    [Fact]
    public void BuildAndPopulatePreserveConstructionAndDeferredRootOwnership()
    {
        const string extra = """
            namespace Fixture {
              public static class Arguments { public static int Reads; public static int Next => ++Reads; }
              public class SeededPanel : Panel { public SeededPanel(int seed) { Seed=seed; } public int Seed {get;} }
            }
            """;
        using var code = CompiledXaml.Create("<SeededPanel " + Namespaces + " Title='before' Value='{Root}'>" +
            "<x:Arguments><x:Static Member='Arguments.Next'/></x:Arguments>" +
            "<Template><Panel Value='{Root}'/></Template></SeededPanel>", Model + extra);
        var built = code.Build();
        var existing = Activator.CreateInstance(code.Assembly.GetType("Fixture.SeededPanel")!, new object[] { 99 })!;
        code.Assembly.GetType(code.Emission.FactoryMetadataName)!.GetMethod(code.Emission.PopulateMethodName)!
            .Invoke(null, new object?[] { existing, null });
        Assert.Equal(1, code.Assembly.GetType("Fixture.Arguments")!.GetField("Reads")!.GetValue(null));
        Assert.Equal(1, Property(built, "Seed"));
        Assert.Equal(99, Property(existing, "Seed"));
        foreach (var root in new[] { built, existing })
        {
            Assert.Same(root, Property(root, "Value"));
            var template = ((IList)Property(root, "Children")!)[0]!;
            var factory = (Delegate)Property(template, "Content")!;
            Assert.Same(root, Property(factory.DynamicInvoke(new object?[] { null })!, "Value"));
            Assert.True(XamlRuntimeSession.TryGet(root, out var session));
            var node = session!.FindNode(root)!;
            Assert.NotNull(node.Source);
            Assert.True(session.Apply(0, new[] { new XamlPropertyUpdate(node.Key, "Title", "after") }).Applied);
            Assert.Equal("after", Property(root, "Title"));
        }
    }
    [Fact]
    public void GeneratedPropertySettersSupportLiveTransactions()
    {
        using var code = CompiledXaml.Create("<Panel " + Namespaces + " Title='before'/>", Model); var root = code.Build();
        Assert.True(XamlRuntimeSession.TryGet(root, out var session)); var node = Assert.Single(session!.Nodes);
        Assert.True(session.Apply(0, new[] { new XamlPropertyUpdate(node.Key, "Title", "after") }).Applied); Assert.Equal("after", Property(root, "Title"));
    }
}
