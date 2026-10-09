using XamlG.Syntax;
using Xunit;
namespace XamlG.Tests;

public sealed class SyntaxTests
{
    [Theory]
    [InlineData("<Root/>")]
    [InlineData("<?xml version='1.0'?>\r\n<!--hello--><Root A='&amp;' >a<![CDATA[<b>]]>c</Root>")]
    [InlineData("<Root><Child /></Root>")]
    public void RoundTripsEverySourceCharacter(string source)
    { var syntax = XamlSyntaxTree.Parse(source); Assert.False(syntax.HasErrors, string.Join(";", syntax.Diagnostics)); Assert.Equal(source, syntax.ToString()); }
    [Fact]
    public void PreservesAttributeQuotesAndTrivia()
    {
        const string source = "<!--keep--><Root  Text = 'old' Other=\"yes\" />"; var tree = XamlSyntaxTree.Parse(source);
        var change = XamlSyntaxEditor.SetAttribute(tree, tree.Root!, "Text", "a'b&c");
        var edited = tree.WithChanges(new[] { change }, tree.Version);
        Assert.Equal("<!--keep--><Root  Text = 'a&apos;b&amp;c' Other=\"yes\" />", edited.Text);
        Assert.Equal("a'b&c", edited.Root!.Attributes[0].Value); Assert.Equal(1, edited.Version);
    }
    [Fact]
    public void RenameChangesBothTagsOnly()
    {
        var tree = XamlSyntaxTree.Parse("<Old Text='Old'><!--Old--></Old>");
        var result = tree.WithChanges(XamlSyntaxEditor.RenameElement(tree, tree.Root!, "New"), 0);
        Assert.Equal("<New Text='Old'><!--Old--></New>", result.Text);
    }
    [Fact]
    public void RejectsStaleAndOverlappingEdits()
    {
        var tree = XamlSyntaxTree.Parse("<Root/>");
        Assert.Throws<InvalidOperationException>(() => tree.WithChanges(Array.Empty<XamlTextChange>(), 1));
        Assert.Throws<ArgumentException>(() => tree.WithChanges(new[] { new XamlTextChange(new(1, 3), "A"), new XamlTextChange(new(2, 1), "B") }, 0));
    }
    [Fact]
    public void NamespaceShadowingIsPersistent()
    {
        var tree = XamlSyntaxTree.Parse("<a:Root xmlns:a='one'><a:Child xmlns:a='two'/></a:Root>");
        var rootScope = NamespaceScope.Empty.Push(tree.Root!); var childScope = rootScope.Push((XamlElementSyntax)tree.Root!.Children[0]);
        Assert.Equal("one", rootScope.Expand("a:T").Namespace); Assert.Equal("two", childScope.Expand("a:T").Namespace);
    }
    [Fact]
    public void SpaceAndIgnorableScopesRemainIndependentWhenBindingsAreUnchanged()
    {
        var tree = XamlSyntaxTree.Parse("<Root xmlns:mc='http://schemas.openxmlformats.org/markup-compatibility/2006' xmlns:a='urn:a'><Child xml:space='preserve' mc:Ignorable='a'><Leaf xml:space='default'/></Child><Sibling/></Root>");
        var root = NamespaceScope.Empty.Push(tree.Root!);
        var childNode = (XamlElementSyntax)tree.Root!.Children[0];
        var child = root.Push(childNode); var leaf = child.Push((XamlElementSyntax)childNode.Children[0]);
        var sibling = root.Push((XamlElementSyntax)tree.Root.Children[1]);
        Assert.False(root.PreserveSpace); Assert.True(child.PreserveSpace); Assert.False(leaf.PreserveSpace);
        Assert.Empty(root.IgnoredNamespaces); Assert.Contains("urn:a", child.IgnoredNamespaces); Assert.Contains("urn:a", leaf.IgnoredNamespaces);
        Assert.False(sibling.PreserveSpace); Assert.Empty(sibling.IgnoredNamespaces);
    }
    [Theory]
    [InlineData("<Root><Child></Root>")]
    [InlineData("<Root A='unfinished")]
    [InlineData("<Root/><Second/>")]
    [InlineData("<Root A='1' A='2'/>")]
    [InlineData("<!DOCTYPE Root [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><Root>&x;</Root>")]
    public void ReportsMalformedInputWithoutThrowing(string source) => Assert.True(XamlSyntaxTree.Parse(source).HasErrors);
    [Fact]
    public void RecoveryDoesNotConsumeParentsClosingTag()
    {
        var tree = XamlSyntaxTree.Parse("<Root><Child></Root>"); Assert.True(tree.HasErrors);
        Assert.Equal("</Root>", tree.Text.Substring(tree.Root!.CloseTagSpan.Start, tree.Root.CloseTagSpan.Length));
    }
    [Fact]
    public void HonorsCancellationAndLimits()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => XamlSyntaxTree.Parse("<Root/>", cancellationToken: cancellation.Token));
        Assert.True(XamlSyntaxTree.Parse(new string('<', 100), options: new(MaximumCharacters: 10)).HasErrors);
        Assert.True(XamlSyntaxTree.Parse("<A><B><C><D/></C></B></A>", options: new(MaximumDepth: 1)).HasErrors);
    }
    [Fact]
    public void MarkupArgumentsRespectNestedBracesAndQuotes()
    {
        const string text = "{Outer {Inner Value='a,b'}, Name='x=y', More={Other A=1, B=2}}"; var diagnostics = new List<XamlDiagnostic>();
        var syntax = MarkupExtensionParser.Parse(text, new(0, text.Length), diagnostics.Add)!;
        Assert.Empty(diagnostics); Assert.Equal(3, syntax.Arguments.Length); Assert.Null(syntax.Arguments[0].Name); Assert.Equal("x=y", syntax.Arguments[1].Value);
    }
    [Fact]
    public void ParsesNestedGenericAndNullableTypes()
    {
        var errors = new List<XamlDiagnostic>(); var type = XamlTypeNameParser.Parse("g:Map(x:String, g:List(x:Int32?))", new(0, 33), errors.Add);
        Assert.Empty(errors); Assert.Equal(2, type!.Arguments.Length); Assert.True(type.Arguments[1].Arguments[0].Nullable);
    }
    [Fact]
    public void SimpleTypeNamesAndFallbackGrammarRetainTheirSourceSpans()
    {
        foreach (var text in new[] { "Button", "a:Button", "A.B", "A+B", "A`1", "名" })
        {
            var errors = new List<XamlDiagnostic>();
            var type = XamlTypeNameParser.Parse(text, new(17, text.Length), errors.Add)!;
            Assert.Empty(errors); Assert.Equal(text, type.Name); Assert.Empty(type.Arguments); Assert.False(type.Nullable);
            Assert.Equal(new TextSpan(17, text.Length), type.Span);
            Assert.Equal(type, Assert.Single(XamlTypeNameParser.ParseList(text, new(17, text.Length), errors.Add)));
        }
        foreach (var (text, offset, length, nullable) in new[] { ("  Foo  ", 2, 5, false), ("Foo?  ", 0, 4, true), ("\u2003Foo\u00a0", 1, 4, false) })
        {
            var errors = new List<XamlDiagnostic>();
            var type = XamlTypeNameParser.Parse(text, new(17, text.Length), errors.Add)!;
            Assert.Empty(errors); Assert.Equal("Foo", type.Name); Assert.Equal(nullable, type.Nullable);
            Assert.Equal(new TextSpan(17 + offset, length), type.Span);
        }
        foreach (var text in new[] { "", " ", "Foo Bar", "Foo??", "Foo,", "Foo)", "Foo()" })
        {
            var errors = new List<XamlDiagnostic>();
            Assert.Null(XamlTypeNameParser.Parse(text, new(17, text.Length), errors.Add));
            Assert.Contains(errors, diagnostic => diagnostic.Code == "XG0011");
        }
    }

    [Fact]
    public void LineMapUsesUtf16AndRecognizesCrLf()
    { var map = new SourceLineMap("a\r\nb\nc"); Assert.Equal(new SourceLinePosition(1, 0), map.GetPosition(3)); Assert.Equal(5, map.GetOffset(new(2, 0))); }
}
