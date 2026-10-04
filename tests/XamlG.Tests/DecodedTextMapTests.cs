using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class DecodedTextMapTests
{
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
