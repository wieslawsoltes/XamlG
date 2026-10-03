using System.Text.Json;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class IncrementalSyntaxTests
{
    [Fact]
    public void LocalPropertyEditReusesUnchangedSubtrees()
    {
        var source = "<Root>" + string.Concat(Enumerable.Range(0, 2000).Select(i => $"<Item Text='{i:0000}'/>")) + "</Root>";
        var tree = XamlSyntaxTree.Parse(source);
        var target = (XamlElementSyntax)tree.Root!.Children[1000];
        var edit = XamlSyntaxEditor.SetAttribute(tree, target, "Text", "abcd");
        var updated = tree.WithChanges(new[] { edit }, 0);
        Assert.True(updated.Statistics.IsIncremental);
        Assert.True(updated.Statistics.ParsedCharacters < 30);
        Assert.Equal(1999, updated.Statistics.ReusedNodes);
        Assert.Same(tree.Root.Children[500], updated.Root!.Children[500]);
        Assert.Same(tree.Root.Children[1500], updated.Root.Children[1500]);
        Assert.Equal(Serialize(XamlSyntaxTree.Parse(updated.Text)), Serialize(updated));
    }

    [Fact]
    public void PositionShiftsPreserveAllSpansAndAncestors()
    {
        var tree = XamlSyntaxTree.Parse("<!--start--><Root><A/><Container><B X='old'/><C/></Container><D/></Root><!--end-->");
        var target = tree.Root!.DescendantsAndSelf().Single(e => e.Name == "B");
        var updated = tree.WithChanges(new[] { XamlSyntaxEditor.SetAttribute(tree, target, "X", "much longer") }, 0);
        Assert.True(updated.Statistics.IsIncremental);
        Assert.Equal(Serialize(XamlSyntaxTree.Parse(updated.Text)), Serialize(updated));
    }

    [Fact]
    public void RecoverySensitiveEditsMatchTheFullParser()
    {
        var random = new Random(3217);
        const string source = "<?xml version='1.0'?><Root><!--comment--><A Text='one'/><B><C X='two'/></B><D/></Root>";
        var tree = XamlSyntaxTree.Parse(source);
        string[] replacements = { "<", ">", "'", "\"", "", "abc", "<!--x-->", "<Child/>", "&amp;", "</B>" };
        for (var i = 0; i < 1000; i++)
        {
            var start = random.Next(source.Length);
            var length = random.Next(Math.Min(5, source.Length - start) + 1);
            var updated = tree.WithChanges(new[] { new XamlTextChange(new(start, length), replacements[random.Next(replacements.Length)]) }, 0);
            Assert.Equal(Serialize(XamlSyntaxTree.Parse(updated.Text)), Serialize(updated));
        }
    }

    private static string Serialize(XamlSyntaxTree tree) => JsonSerializer.Serialize(new
    {
        nodes = tree.Nodes.Select(Project),
        diagnostics = tree.Diagnostics
    });
    private static object Project(XamlSyntaxNode node) => node switch
    {
        XamlElementSyntax e => new { e.Name, e.Span, e.NameSpan, e.OpenTagSpan, e.CloseTagSpan, e.EndNameSpan, e.Attributes, Children = e.Children.Select(Project).ToArray() },
        XamlTextSyntax text => new { text.Value, text.Span, text.IsCData },
        XamlTriviaSyntax trivia => new { trivia.Kind, trivia.Span },
        _ => throw new InvalidOperationException()
    };
}
