using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ElementLookupTests
{
    [Theory]
    [InlineData("")]
    [InlineData("<Root/>")]
    [InlineData("  <Root><Short/><Longer Name='x'/><End/></Root>  ")]
    [InlineData("<Root><A><B/></A><C><D/></C></Root>")]
    [InlineData("<Root><!--comment--><![CDATA[<literal/>]]><Child/>text</Root>")]
    [InlineData("<Root><A></Wrong><B/>")]
    [InlineData("<Root><A><B/><C")]
    [InlineData("<Root Value='unfinished><Child/>")]
    [InlineData("<Root/><Other/>")]
    public void IndexedLookupMatchesSmallestContainingElementAtEveryCursorPosition(string source)
    {
        Check(XamlSyntaxTree.Parse(source));
    }

    [Fact]
    public void IndexedLookupMatchesNestedAndAdjacentElementsAcrossRandomDocuments()
    {
        var random = new Random(739);
        for (var sample = 0; sample < 60; sample++)
        {
            var source = new System.Text.StringBuilder("<Root>");
            var depth = 0;
            for (var i = 0; i < 40; i++)
            {
                if (depth != 0 && random.Next(3) == 0) { source.Append("</Node>"); depth--; }
                else if (random.Next(3) == 0) { source.Append("<Node>"); depth++; }
                else source.Append("<Leaf Text='value'/>");
            }
            for (; depth > 0; depth--) source.Append("</Node>");
            source.Append("</Root>");
            Check(XamlSyntaxTree.Parse(source.ToString()));
        }
    }

    [Fact]
    public void ConcurrentFirstQueriesAndEditedSnapshotsKeepTheirOwnSourceLocations()
    {
        const string source = "<Root><A/><Longer/><B/></Root>";
        var original = XamlSyntaxTree.Parse(source);
        Parallel.For(0, source.Length + 1, position => Assert.Same(Reference(original, position), original.FindElement(position)));
        var changed = original.WithChanges([new(new(source.IndexOf("<A/>", StringComparison.Ordinal), 4), "<A><Nested/></A>")], original.Version);
        Check(changed);
        Check(original);
        Assert.NotSame(original.FindElement(7), changed.FindElement(7));
    }

    private static void Check(XamlSyntaxTree tree)
    {
        for (var position = -1; position <= tree.Text.Length + 1; position++)
            Assert.Same(Reference(tree, position), tree.FindElement(position));
    }

    private static XamlElementSyntax? Reference(XamlSyntaxTree tree, int position) => tree.Root?.DescendantsAndSelf()
        .Where(element => element.Span.Start <= position && element.Span.End >= position)
        .OrderBy(element => element.Span.Length).FirstOrDefault();
}
