using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class IntrinsicTypeProbeTests
{
    private const string Model = """
        namespace Demo;
        public class View
        {
            public object Value { get; set; }
            public object Other { get; set; }
            public Items Items { get; } = new();
        }
        public class Item { }
        public class Items
        {
            public object Value { get; private set; }
            public void Add(string value) => Value = value;
            public void Add(Item value) => Value = value;
            public void Add(int[] value) => Value = value;
        }
        public class ArrayHolder
        {
            public object Value { get; }
            public ArrayHolder(string value) => Value = value;
            public ArrayHolder(int[] value) => Value = value;
        }
        public class ReferenceHolder
        {
            public object Value { get; }
            public ReferenceHolder(string value) => Value = value;
            public ReferenceHolder(Item value) => Value = value;
        }
        public class Strings { public void Add(string value) { } }
        public static class Statics { public static int Number => 42; }
        """;
    private const string Prefix = "<View xmlns='clr-namespace:Demo' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>";

    [Theory]
    [InlineData("x:Int32")]
    [InlineData("{x:Type x:Int32}")]
    public void ArraysSelectTheMatchingCollectionOverload(string type)
    {
        using var code = CompiledXaml.Create(Prefix + "<View.Items><x:Array Type='" + type + "'><x:Int32>42</x:Int32></x:Array></View.Items></View>", Model);
        var root = code.Build();
        Assert.Equal(new[] { 42 }, Assert.IsType<int[]>(Get(Get(root, "Items"), "Value")));
    }

    [Fact]
    public void ArraysSelectTheMatchingConstructor()
    {
        using var code = CompiledXaml.Create(Prefix + "<View.Value><ArrayHolder><x:Arguments><x:Array Type='x:Int32'><x:Int32>42</x:Int32></x:Array></x:Arguments></ArrayHolder></View.Value></View>", Model);
        var root = code.Build();
        Assert.Equal(new[] { 42 }, Assert.IsType<int[]>(Get(Get(root, "Value"), "Value")));
    }

    [Fact]
    public void KnownReferencesSelectTheMatchingConstructor()
    {
        using var code = CompiledXaml.Create(Prefix + "<View.Other><Item x:Name='known'/></View.Other><View.Value><ReferenceHolder><x:Arguments><x:Reference Name='known'/></x:Arguments></ReferenceHolder></View.Value></View>", Model);
        var root = code.Build();
        Assert.Same(Get(root, "Other"), Get(Get(root, "Value"), "Value"));
    }

    [Fact]
    public void ForwardReferencesRetainRuntimeCollectionDispatch()
    {
        using var code = CompiledXaml.Create(Prefix + "<View.Items><x:Reference Name='later'/></View.Items><View.Other><Item x:Name='later'/></View.Other></View>", Model);
        var root = code.Build();
        Assert.Same(Get(root, "Other"), Get(Get(root, "Items"), "Value"));
    }

    [Fact]
    public void ProbingAnInvalidStaticMemberDoesNotDuplicateDiagnostics()
    {
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Strings xmlns='clr-namespace:Demo' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><x:Static Member='Statics.Missing'/></Strings>"), CompilationFactory.Create(Model));
        Assert.Equal("XG1009", Assert.Single(document.Diagnostics.Where(diagnostic => diagnostic.Severity == XamlSeverity.Error)).Code);
    }

    private static object Get(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target)!;
}
