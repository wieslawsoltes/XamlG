using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class DecodedTextMapTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("m:Box(m:Item)", true)]
    [InlineData("literal 😀 value", true)]
    [InlineData("line\nwith\ttabs", false)]
    public void UnchangedTextMapsEveryUtf16RangeWithoutNormalization(string text, bool attribute)
    {
        var source = "prefix " + text + " suffix";
        var map = XamlDecodedTextMap.Create(source, new(7, text.Length), attribute);
        Assert.Equal(text, map.Text);
        for (var start = 0; start <= text.Length; start++)
            for (var end = start; end <= text.Length; end++)
                Assert.Equal(new(7 + start, end - start), map.ToSource(TextSpan.FromBounds(start, end)));
        Assert.Throws<ArgumentException>(() => map.ToSource(new(text.Length, 1)));
        Assert.Throws<ArgumentException>(() => map.ToSource(new(text.Length + 1, 0)));
    }

    [Theory]
    [InlineData(true, "a  b c d&😀")]
    [InlineData(false, "a\t\nb\nc\nd&😀")]
    public void NormalizedTextRetainsEntityAndLineEndingBoundaries(bool attribute, string expected)
    {
        const string value = "a\t\nb\r\nc\rd&amp;😀";
        var map = XamlDecodedTextMap.Create("prefix " + value + " suffix", new(7, value.Length), attribute);
        Assert.Equal(expected, map.Text);
        Assert.Equal(new(7, value.Length), map.ToSource(new(0, expected.Length)));
        Assert.Equal(new(11, 2), map.ToSource(new(4, 1)));
        // A literal surrogate pair retains ordinary UTF-16 boundaries even on the
        // decoded path; only a pair produced by one numeric entity is indivisible.
        Assert.Equal(new(7 + value.Length - 2, 1), map.ToSource(new(expected.Length - 2, 1)));
        Assert.Throws<ArgumentException>(() => map.ToSource(new(expected.Length, 1)));
    }

    [Fact]
    public void NumericEntitiesAndXmlAttributeNormalizationMapBackExactly()
    {
        const string source = "prefix a\r\nb&#x1F600;c&amp;d suffix";
        var map = XamlDecodedTextMap.Create(source, new(7, source.Length - 14));
        Assert.Equal("a b😀c&d", map.Text);
        var span = map.ToSource(new(3, 2));
        Assert.Equal("&#x1F600;", source.Substring(span.Start, span.Length));
        Assert.Throws<ArgumentException>(() => map.ToSource(new(3, 1)));
        Assert.Equal("&amp;", source.Substring(map.ToSource(new(6, 1)).Start, map.ToSource(new(6, 1)).Length));
    }
    [Fact]
    public void MarkupArgumentValueRangesExcludeNamesWhitespaceAndQuotes()
    {
        const string text = "{x:Reference Name = 'target' }";
        var parsed = MarkupExtensionParser.Parse(text, new(10, text.Length), _ => { })!;
        var span = Assert.Single(parsed.Arguments).ValueSpan!.Value;
        Assert.Equal("target", text.Substring(span.Start - 10, span.Length));
    }
    [Fact]
    public void NestedMarkupMapsTheNameAfterEscapedQuotes()
    {
        var tree = XamlSyntaxTree.Parse("<Root Value=\"{Wrap Prefix=&quot;a,b&quot;, Value={x:Reference Name='t&#97;rget'}}\"/>");
        var occurrences = XamlMarkupScanner.Scan(tree, tree.Root!.Attributes[0]).ToArray();
        var reference = occurrences.Single(o => o.Syntax.Name == "x:Reference");
        var span = reference.SourceMap.ToSource(reference.Syntax.Arguments[0].ValueSpan!.Value);
        Assert.Equal("t&#97;rget", tree.Text.Substring(span.Start, span.Length));
    }
}
