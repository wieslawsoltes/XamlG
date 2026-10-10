using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class WideElementParserTests
{
    [Fact]
    public void Wide_elements_do_not_exhaust_the_name_budget_for_subsequent_repeated_children()
    {
        var attributes = string.Join(" ", Enumerable.Range(0, 1000).Select(i => "A" + i + "='v'"));
        var source = "<Root " + attributes + "><Child Common='first'/><Child Common='second'/></Root>";
        var tree = XamlSyntaxTree.Parse(source);
        Assert.Empty(tree.Diagnostics);
        Assert.Equal(1000, tree.Root!.Attributes.Length);
        var children = tree.Root.Children.OfType<XamlElementSyntax>().ToArray();
        Assert.Same(children[0].Name, children[1].Name);
        Assert.Same(children[0].Attributes[0].Name, children[1].Attributes[0].Name);
        foreach (var attribute in tree.Root.Attributes)
            Assert.Equal(attribute.Name, source.Substring(attribute.NameSpan.Start, attribute.NameSpan.Length));
    }

    [Theory]
    [InlineData("A0")]
    [InlineData("A7")]
    [InlineData("A8")]
    [InlineData("A999")]
    public void Wide_elements_detect_duplicates_across_atomized_and_non_atomized_names(string duplicate)
    {
        var attributes = string.Join(" ", Enumerable.Range(0, 1000).Select(i => "A" + i + "='v'"));
        var source = "<Root " + attributes + " " + duplicate + "='duplicate'/>";
        var tree = XamlSyntaxTree.Parse(source);
        var diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("XG0006", diagnostic.Code);
        Assert.Equal(duplicate, source.Substring(diagnostic.Span.Start, diagnostic.Span.Length));
    }
}
