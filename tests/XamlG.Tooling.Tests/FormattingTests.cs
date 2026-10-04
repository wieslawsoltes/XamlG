using XamlG.Syntax;
using XamlG.Tooling.Formatting;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class FormattingTests
{
    private static string Format(string source, XamlFormattingOptions? options = null)
    {
        var syntax = XamlSyntaxTree.Parse(source);
        return syntax.WithChanges(XamlFormatter.Format(syntax, options), syntax.Version).Text;
    }
    [Fact]
    public void ElementOnlyContentIsIndentedAndFormattingIsIdempotent()
    {
        const string source = "<Root a = 'v'><Child/><Child><Leaf /></Child><!--note--></Root>";
        var formatted = Format(source, new() { TabSize = 2 });
        Assert.Equal("<Root a='v'>\n  <Child />\n  <Child>\n    <Leaf />\n  </Child>\n  <!--note-->\n</Root>", formatted);
        Assert.Equal(formatted, Format(formatted, new() { TabSize = 2 }));
    }
    [Theory]
    [InlineData("<Root>one <Child /> two</Root>")]
    [InlineData("<Root><![CDATA[a\r\nb]]><Child /></Root>")]
    [InlineData("<Root xml:space='preserve'>  <Child /> \n </Root>")]
    public void LiteralMixedAndPreservedContentIsUnchanged(string source) => Assert.Equal(source, Format(source));
    [Fact]
    public void AttributeValuesKeepEntitiesQuotesAndLineEndings()
    {
        const string source = "<Root A = 'a&#10;b&amp;c' B=\"{x:Reference Name='foo'}\" C='x\r\ny'/>";
        var formatted = Format(source);
        Assert.Contains("A='a&#10;b&amp;c'", formatted);
        Assert.Contains("B=\"{x:Reference Name='foo'}\"", formatted);
        Assert.Contains("C='x\r\ny'", formatted);
    }
    [Fact]
    public void RangeFormattingCannotEditOutsideTheRequestedRange()
    {
        var source = XamlSyntaxTree.Parse("<Root><One A = 'x'/><Two B = 'y'/></Root>");
        var child = source.Root!.Children.OfType<XamlElementSyntax>().First();
        var edits = XamlFormatter.Format(source, range: child.Span);
        Assert.All(edits, e => Assert.True(child.Span.Contains(e.Span)));
        var formatted = source.WithChanges(edits, source.Version).Text;
        Assert.Contains("<One A='x' />", formatted);
        Assert.Contains("<Two B = 'y'/>", formatted);
    }
    [Fact]
    public void MalformedDocumentsAreNotReformatted() => Assert.Empty(XamlFormatter.Format(XamlSyntaxTree.Parse("<Root A='unterminated>")));
    [Fact]
    public void TabsCrLfAndFinalNewlineAreRespected()
    {
        var formatted = Format("<Root>\r\n<Child/>\r\n</Root>", new() { InsertSpaces = false, InsertFinalNewline = true });
        Assert.Equal("<Root>\r\n\t<Child />\r\n</Root>\r\n", formatted);
    }
}
