using XamlX;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class CollectionOverloadTests : CompilerTestBase
{
    private const string Namespace = " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [Theory]
    [InlineData("ObjectFirstCollection", "text", "object")]
    [InlineData("ObjectFirstCollection", "<x:String>text</x:String>", "object")]
    [InlineData("NumberConversionCollection", "42", "int:42")]
    [InlineData("NumberConversionCollection", "<x:String>42</x:String>", "int:42")]
    [InlineData("InheritedCollection", "text", "object")]
    [InlineData("ItemOverloadCollection", "<SpecialItem/>", "base")]
    public void PreservesSetterAndConversionOrdering(string type, string content, string expected)
    {
        var root = (SelectionCollection)CompileAndRun("<" + type + Namespace + ">" + content + "</" + type + ">");
        Assert.Equal(expected, root.Selected);
    }

    [Fact]
    public void DictionaryKeysAreConvertedBeforeSelectingTheValueOverload()
    {
        Assert.Throws<XamlLoadException>(() => Compile("<IncompatibleKeyCollection" + Namespace + "><x:String x:Key='42'>text</x:String></IncompatibleKeyCollection>"));
    }

    [Fact]
    public void InvalidNumericTextDoesNotFallBackToAnEarlierStringAdder()
    {
        Assert.ThrowsAny<XamlParseException>(() => Compile("<NumberConversionCollection" + Namespace + ">invalid</NumberConversionCollection>"));
    }

    [Fact]
    public void NullableNumericWideningDoesNotRemoveRuntimeAlternatives()
    {
        var root = (NullableNumberCollection)CompileAndRun("<NullableNumberCollection" + Namespace + "><IntegerValue/></NullableNumberCollection>");
        Assert.Equal("int", root.Selected);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("<x:String>42</x:String>")]
    public void AConvertedCollectionSetterPrecedesItsAdder(string value)
    {
        var root = (ConvertibleCollectionRoot)CompileAndRun("<ConvertibleCollectionRoot" + Namespace + "><ConvertibleCollectionRoot.Items>" + value + "</ConvertibleCollectionRoot.Items></ConvertibleCollectionRoot>");
        Assert.Equal("parsed:42", root.Items.Selected);
    }
}

public class SelectionCollection
{
    public string? Selected { get; protected set; }
}

public sealed class ObjectFirstCollection : SelectionCollection
{
    public void Add(object value) => Selected = "object";
    public void Add(string value) => Selected = "string";
}

public sealed class NumberConversionCollection : SelectionCollection
{
    public void Add(string value) => Selected = "string";
    public void Add(int value) => Selected = "int:" + value;
}

public class StringCollectionBase : SelectionCollection
{
    public void Add(string value) => Selected = "string";
}

public sealed class InheritedCollection : StringCollectionBase
{
    public void Add(object value) => Selected = "object";
}

public class BaseItem { }
public sealed class SpecialItem : BaseItem { }

public sealed class ItemOverloadCollection : SelectionCollection
{
    public void Add(BaseItem value) => Selected = "base";
    public void Add(SpecialItem value) => Selected = "derived";
}

public sealed class IncompatibleKeyCollection
{
    public void Add(int key, int value) { }
    public void Add(string key, string value) { }
}

public sealed class NullableNumberCollection : SelectionCollection
{
    public void Add(long? value) => Selected = "long";
    public void Add(int? value) => Selected = "int";
}

public sealed class IntegerValue
{
    public object ProvideValue() => 42;
}

public sealed class ConvertibleCollectionRoot
{
    public ConvertibleCollection Items { get; set; } = new();
}

public sealed class ConvertibleCollection : SelectionCollection
{
    public void Add(string value) => Selected = "added:" + value;
    public static ConvertibleCollection Parse(string value) => new() { Selected = "parsed:" + value };
}
