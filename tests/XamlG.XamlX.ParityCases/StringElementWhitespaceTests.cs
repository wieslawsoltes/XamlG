using System.Collections.Generic;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class StringElementWhitespaceTests : CompilerTestBase
{
    private const string Ns = " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [Theory]
    [InlineData("Text", " first  second ", false, "first second")]
    [InlineData("Text", " ", false, "initial")]
    [InlineData("Text", " first\nsecond\tlast ", false, "first second last")]
    [InlineData("Text", " first  second ", true, " first  second ")]
    [InlineData("Text", " ", true, " ")]
    [InlineData("Boxed", " first  second ", false, "first second")]
    [InlineData("Boxed", " ", false, "initial")]
    [InlineData("Boxed", " first\nsecond\tlast ", false, "first second last")]
    [InlineData("Boxed", " first  second ", true, " first  second ")]
    [InlineData("Boxed", " ", true, " ")]
    [InlineData("Converted", " first  second ", false, "first second")]
    [InlineData("Converted", " ", false, "initial")]
    [InlineData("Converted", " first  second ", true, " first  second ")]
    public void StringElementsNormalizeBeforeAssignmentAndConversion(string property, string text, bool preserve, string expected)
    {
        var xaml = "<StringWhitespaceContainer" + Ns + "><StringWhitespaceContainer." + property + "><x:String" +
            (preserve ? " xml:space='preserve'" : "") + ">" + text + "</x:String></StringWhitespaceContainer." + property + "></StringWhitespaceContainer>";
        var root = (StringWhitespaceContainer)CompileAndRun(xaml);
        Assert.Equal(expected, property switch { "Text" => root.Text, "Boxed" => root.Boxed, _ => root.Converted.Text });
    }

    [Theory]
    [InlineData(false, false, false, 2)]
    [InlineData(false, true, false, 2)]
    [InlineData(true, false, false, 2)]
    [InlineData(true, true, false, 2)]
    [InlineData(false, false, true, 2)]
    [InlineData(false, true, true, 2)]
    [InlineData(true, false, true, 3)]
    [InlineData(true, true, true, 3)]
    public void CollectionsRetainTheirWhitespaceSignificance(bool significant, bool preserve, bool property, int count)
    {
        var type = significant ? "InheritedWhitespace" : "StringWhitespaceList";
        var xaml = "<" + type + Ns + "><PlainMetadataItem/><x:String" + (preserve ? " xml:space='preserve'" : "") +
            "> </x:String><PlainMetadataItem/></" + type + ">";
        if (property)
        {
            var member = significant ? "Significant" : "Items";
            xaml = "<StringWhitespaceContainer" + Ns + "><StringWhitespaceContainer." + member + "><PlainMetadataItem/><x:String" +
                (preserve ? " xml:space='preserve'" : "") + "> </x:String><PlainMetadataItem/></StringWhitespaceContainer." + member + "></StringWhitespaceContainer>";
        }
        var result = CompileAndRun(xaml);
        var root = result is StringWhitespaceContainer container ? significant ? container.Significant : container.Items : (List<object>)result;
        Assert.Equal(count, root.Count);
        if (count == 3) Assert.Equal(" ", root[1]);
    }
}

public sealed class StringWhitespaceContainer
{
    public string Text { get; set; } = "initial";
    public object Boxed { get; set; } = "initial";
    public StringWhitespaceConversion Converted { get; set; } = new("initial");
    public List<object> Items { get; } = new();
    public InheritedWhitespace Significant { get; } = new();
}

public sealed class StringWhitespaceConversion
{
    public StringWhitespaceConversion(string text) => Text = text;
    public string Text { get; }
    public static StringWhitespaceConversion Parse(string text) => new(text);
}

public sealed class StringWhitespaceList : List<object> { }
