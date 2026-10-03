using XamlG.Syntax;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class TextDifferTests
{
    [Theory]
    [InlineData("", "<A/>")]
    [InlineData("<A/>", "")]
    [InlineData("<A Text='😀'/>", "<A Text='😁'/>")]
    [InlineData("<A Text='😀'/>", "<A Text='🨀'/>")]
    [InlineData("<A>\r\n<B/>\r\n</A>", "<A>\n<B/>\n</A>")]
    public void ReplacementReconstructsTheExactBuffer(string previous, string current)
    {
        var change = Assert.Single(XamlTextDiffer.GetChanges(previous, current));
        var result = previous.Substring(0, change.Span.Start) + change.NewText + previous.Substring(change.Span.End);
        Assert.Equal(current, result);
        Assert.False(SplitsPair(previous, change.Span.Start));
        Assert.False(SplitsPair(previous, change.Span.End));
        Assert.False(SplitsPair(current, change.Span.Start));
        Assert.False(SplitsPair(current, change.Span.Start + change.NewText.Length));
    }

    [Fact]
    public void AnIdenticalBufferDoesNotCreateARevision()
    {
        var tree = XamlSyntaxTree.Parse("<A/>");
        Assert.Same(tree, tree.WithChanges(XamlTextDiffer.GetChanges(tree.Text, tree.Text), tree.Version));
    }

    [Fact]
    public void FullEditorBuffersStillUseIncrementalSubtreeParsing()
    {
        var previous = "<Root><A Text='old'/><B Text='retained'/></Root>";
        var current = previous.Replace("old", "new");
        var session = new XamlDocumentSession(previous);
        var tree = session.Current;
        var updated = session.Apply(new(tree.Version, "Editor buffer", XamlTextDiffer.GetChanges(previous, current)));
        Assert.True(updated.Statistics.IsIncremental);
        Assert.Same(tree.Root!.Children[1], updated.Root!.Children[1]);
        Assert.Equal(current, updated.Text);
    }

    [Fact]
    public void CancellationIsObservedBeforeDiffing()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => XamlTextDiffer.GetChanges("", "", source.Token));
    }

    private static bool SplitsPair(string value, int offset) => offset > 0 && offset < value.Length &&
        char.IsHighSurrogate(value[offset - 1]) && char.IsLowSurrogate(value[offset]);
}
