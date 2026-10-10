using System.Xml.Linq;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class SpanXmlParserTests
{
    [Fact]
    public void XmlSpanDecodingMatchesXmlNormalizationForSmallAndPooledBuffers()
    {
        string[] pieces = ["plain", "😀", "\r", "\r\n", "\n", "\t", "&amp;", "&lt;", "&gt;", "&apos;", "&quot;",
            "&#9;", "&#10;", "&#13;", "&#32;", "&#xD7FF;", "&#xE000;", "&#xFFFD;", "&#x10000;", "&#x10FFFF;", "&#000000000000000000000000000032;"];
        var random = new Random(712);
        for (var i = 0; i < 100; i++)
        {
            var raw = "prefix" + string.Concat(Enumerable.Range(0, i).Select(_ => pieces[random.Next(pieces.Length)])) + "suffix";
            var source = "<Root Value='" + raw + "'>" + raw + "</Root>";
            var reference = XElement.Parse(source, LoadOptions.PreserveWhitespace);
            var tree = XamlSyntaxTree.Parse(source);
            Assert.False(tree.HasErrors, string.Join(";", tree.Diagnostics));
            var attribute = Assert.Single(tree.Root!.Attributes);
            Assert.Equal(reference.Attribute("Value")!.Value, attribute.Value);
            Assert.Equal(reference.Value, Assert.IsType<XamlTextSyntax>(Assert.Single(tree.Root.Children)).Value);
            var map = XamlDecodedTextMap.Create(source, attribute.ValueSpan);
            Assert.Equal(attribute.Value, map.Text);
            Assert.Equal(attribute.ValueSpan, map.ToSource(new(0, map.Text.Length)));
        }
    }

    [Theory]
    [InlineData("&unknown;", "Unknown or invalid XML entity '&unknown;'.", 9)]
    [InlineData("&#0;", "Unknown or invalid XML entity '&#0;'.", 4)]
    [InlineData("&#xD800;", "Unknown or invalid XML entity '&#xD800;'.", 8)]
    [InlineData("&#x110000;", "Unknown or invalid XML entity '&#x110000;'.", 10)]
    [InlineData("&unfinished", "Unterminated XML entity.", 1)]
    public void InvalidEntitiesRetainRawTextDiagnosticAndSourceOffset(string raw, string message, int length)
    {
        const string prefix = "<Root Value='before";
        var tree = XamlSyntaxTree.Parse(prefix + raw + "after'/>");
        Assert.Equal("before" + raw + "after", tree.Root!.Attributes[0].Value);
        var diagnostic = Assert.Single(tree.Diagnostics.Where(item => item.Code == "XG0008"));
        Assert.Equal(message, diagnostic.Message);
        Assert.Equal(new TextSpan(prefix.Length, length), diagnostic.Span);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    public void EntitySearchRetainsTheExistingLengthLimitAndEditBoundaries(int nameLength)
    {
        var entity = "&#" + new string('0', nameLength - 3) + "32;";
        const string prefix = "<Root Value='";
        var source = prefix + entity + "&amp;'/>";
        var tree = XamlSyntaxTree.Parse(source);
        var attribute = tree.Root!.Attributes[0];
        if (nameLength <= 31)
        {
            Assert.Empty(tree.Diagnostics);
            Assert.Equal(" &", attribute.Value);
            var map = XamlDecodedTextMap.Create(source, attribute.ValueSpan);
            Assert.Equal(" &", map.Text);
            Assert.Equal(new TextSpan(prefix.Length, entity.Length), map.ToSource(new(0, 1)));
        }
        else
        {
            var diagnostic = Assert.Single(tree.Diagnostics);
            Assert.Equal("Unterminated XML entity.", diagnostic.Message);
            Assert.Equal(new TextSpan(prefix.Length, 1), diagnostic.Span);
            Assert.Equal(entity + "&", attribute.Value);
            Assert.Throws<ArgumentException>(() => XamlDecodedTextMap.Create(source, attribute.ValueSpan));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongUnterminatedEntityRunsRetainEveryDiagnosticAndRawCharacter(bool trailingSemicolon)
    {
        const int count = 65_536;
        var raw = new string('&', count) + (trailingSemicolon ? ";" : "");
        const string prefix = "<Root Value='";
        var tree = XamlSyntaxTree.Parse(prefix + raw + "'/>", options: new(MaximumDiagnostics: count));
        Assert.Equal(raw, tree.Root!.Attributes[0].Value);
        // A nearby semicolon consumes the final 32 ampersands as one invalid entity.
        var expected = trailingSemicolon ? count - 31 : count;
        Assert.Equal(expected, tree.Diagnostics.Length);
        for (var i = 0; i < expected; i++)
        {
            Assert.Equal("XG0008", tree.Diagnostics[i].Code);
            Assert.Equal(prefix.Length + i, tree.Diagnostics[i].Span.Start);
            Assert.Equal(trailingSemicolon && i == expected - 1 ? 33 : 1, tree.Diagnostics[i].Span.Length);
        }
    }
}
