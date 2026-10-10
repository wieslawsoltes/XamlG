using System.Collections;
using System.Reflection;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class NamedFieldIndexTests
{
    private const string Namespaces = "xmlns='clr-namespace:Fixture' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
    private const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(512)]
    public void Interleaved_named_and_unnamed_objects_keep_their_exact_field_targets(int count)
    {
        var children = string.Concat(Enumerable.Range(0, count).Select(i =>
            "<Item Text='unnamed'/><Item x:Name='item" + i + "' Text='named" + i + "'/>"));
        using var code = CompiledXaml.Create("<Panel " + Namespaces + " x:Class='Fixture.Window'>" + children + "</Panel>", EmissionTests.Model);
        var root = Activator.CreateInstance(code.Assembly.GetType("Fixture.Window")!)!;
        root.GetType().GetMethod("InitializeComponent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(root, null);
        var values = (IList)root.GetType().GetProperty("Children")!.GetValue(root)!;
        Assert.Equal(count * 2, values.Count);
        for (var i = 0; i < count; i++)
            Assert.Same(values[i * 2 + 1], root.GetType().GetField("item" + i, Fields)!.GetValue(root));
        Assert.True(XamlRuntimeSession.TryGet(root, out var session));
        session!.Dispose();
    }

    [Fact]
    public void Deferred_names_do_not_leak_into_code_behind_fields()
    {
        using var code = CompiledXaml.Create("<Panel " + Namespaces + " x:Class='Fixture.Window'>" +
            "<Item x:Name='visible'/><Template><Item x:Name='inside'/></Template></Panel>", EmissionTests.Model);
        var root = Activator.CreateInstance(code.Assembly.GetType("Fixture.Window")!)!;
        root.GetType().GetMethod("InitializeComponent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(root, null);
        var children = (IList)root.GetType().GetProperty("Children")!.GetValue(root)!;
        Assert.Same(children[0], root.GetType().GetField("visible", Fields)!.GetValue(root));
        Assert.Null(root.GetType().GetField("inside", Fields));
        Assert.True(XamlRuntimeSession.TryGet(root, out var session));
        session!.Dispose();
    }
}
