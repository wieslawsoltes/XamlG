using Xunit;

namespace XamlG.Tests;

public sealed class ConstructorSelectionTests
{
    [Theory]
    [InlineData("public Choice() { Selected = 1; } public Choice(int value = 7) { Selected = value; }", "", 1)]
    [InlineData("private Choice() { Selected = 1; } public Choice(int value = 7) { Selected = value; }", "", 7)]
    [InlineData("public Choice(System.IServiceProvider services) { Selected = services == null ? -1 : 8; }", "", 8)]
    [InlineData("public Choice() { Selected = 1; } public static Choice Create() => new Choice { Selected = 9 };", " x:FactoryMethod='Create'", 9)]
    public void EmptyArgumentsPreserveAccessibilityOptionalServiceAndFactorySelection(string constructors, string directive, int selected)
    {
        var source = "namespace Constructors { public class Choice { public int Selected { get; private set; } " + constructors + " } }";
        using var code = CompiledXaml.Create("<Choice xmlns='clr-namespace:Constructors' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'" + directive + "/>", source);
        var root = code.Build();
        Assert.Equal(selected, root.GetType().GetProperty("Selected")!.GetValue(root));
    }
}
