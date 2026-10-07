using Xunit;

namespace XamlG.Tests;

public sealed class ArrayEvaluationTests
{
    private const string Model = """
        namespace Demo;
        public class View
        {
            public static System.Collections.Generic.List<string> Log { get; } = new();
            public object Value { get; set; }
            public string Events => string.Join(",", Log);
        }
        public class ArrayValueExtension
        {
            public ArrayValueExtension() => View.Log.Add("new");
            public string Value { get; set; }
            public string ProvideValue() { View.Log.Add("provide:" + Value); return Value; }
        }
        """;
    private const string Prefix = "<View xmlns='clr-namespace:Demo' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><View.Value>";

    [Fact]
    public void ArrayItemsFinishEvaluationBeforeTheNextItemIsConstructed()
    {
        using var compiled = CompiledXaml.Create(Prefix + "<x:Array Type='x:String'><ArrayValueExtension Value='one'/><ArrayValueExtension Value='two'/></x:Array></View.Value></View>", Model);
        var root = compiled.Build();
        Assert.Equal(new[] { "one", "two" }, Assert.IsType<string[]>(root.GetType().GetProperty("Value")!.GetValue(root)));
        Assert.Equal("new,provide:one,new,provide:two", root.GetType().GetProperty("Events")!.GetValue(root));
    }

    [Fact]
    public void JaggedArrayAllocationPreservesEachDimension()
    {
        using var compiled = CompiledXaml.Create(Prefix + "<x:Array Type='x:Int32[]'><x:Array Type='x:Int32'><x:Int32>7</x:Int32></x:Array><x:Array Type='x:Int32'><x:Int32>8</x:Int32><x:Int32>9</x:Int32></x:Array></x:Array></View.Value></View>", Model);
        var root = compiled.Build();
        var value = Assert.IsType<int[][]>(root.GetType().GetProperty("Value")!.GetValue(root));
        Assert.Equal(2, value.Length);
        Assert.Equal(new[] { 7 }, value[0]);
        Assert.Equal(new[] { 8, 9 }, value[1]);
    }
}
