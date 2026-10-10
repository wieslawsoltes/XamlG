using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ParserFastPathTests
{
    [Fact]
    public void Repeated_names_share_parse_local_atoms_without_changing_source_spans()
    {
        const string source = "<Root><p:Item Value='a'/><p:Item Value='b'></p:Item></Root>";
        var syntax = XamlSyntaxTree.Parse(source);
        Assert.Empty(syntax.Diagnostics);
        var children = syntax.Root!.Children.OfType<XamlElementSyntax>().ToArray();
        Assert.Same(children[0].Name, children[1].Name);
        Assert.Same(children[0].Attributes[0].Name, children[1].Attributes[0].Name);
        Assert.NotEqual(children[0].NameSpan, children[1].NameSpan);
        foreach (var child in children)
            Assert.Equal(child.Name, source.Substring(child.NameSpan.Start, child.NameSpan.Length));
        Assert.Equal("p:Item", source.Substring(children[1].EndNameSpan.Start, children[1].EndNameSpan.Length));
    }

    [Fact]
    public void Atom_table_is_case_sensitive_and_bounded_without_rejecting_unique_or_long_names()
    {
        var longName = new string('N', 256);
        var source = "<Root><Name/><name/>" + string.Concat(Enumerable.Range(0, 800).Select(i => "<Node" + i + "/>")) +
            "<" + longName + "></" + longName + "><Name/></Root>";
        var syntax = XamlSyntaxTree.Parse(source);
        Assert.Empty(syntax.Diagnostics);
        var elements = syntax.Root!.Children.OfType<XamlElementSyntax>().ToArray();
        Assert.Equal(804, elements.Length);
        Assert.NotEqual(elements[0].Name, elements[1].Name);
        Assert.Same(elements[0].Name, elements[elements.Length - 1].Name);
        Assert.Equal(longName, elements[elements.Length - 2].Name);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(128)]
    public void Duplicate_attributes_are_reported_before_and_after_hash_table_promotion(int count)
    {
        var attributes = string.Join(" ", Enumerable.Range(0, count).Select(i => "A" + i + "='v'"));
        var source = "<Root " + attributes + " A0='duplicate'/>";
        var syntax = XamlSyntaxTree.Parse(source);
        var diagnostic = Assert.Single(syntax.Diagnostics);
        Assert.Equal("XG0006", diagnostic.Code);
        Assert.Equal("A0", source.Substring(diagnostic.Span.Start, diagnostic.Span.Length));
        Assert.Equal(count + 1, syntax.Root!.Attributes.Length);
    }

    [Theory]
    [InlineData("<Root/>")]
    [InlineData("<Root></Root>")]
    public void Empty_arrays_remain_initialized_for_leaf_elements(string source)
    {
        var root = XamlSyntaxTree.Parse(source).Root!;
        Assert.False(root.Attributes.IsDefault);
        Assert.False(root.Children.IsDefault);
        Assert.Empty(root.Attributes);
        Assert.Empty(root.Children);
    }

    [Theory]
    [InlineData("{Binding}")]
    [InlineData("{Binding Name, Mode=TwoWay}")]
    [InlineData("{Binding Path='A,B', FallbackValue={StaticResource Key}}")]
    [InlineData("{Binding Path='A' Mode='B'}")]
    [InlineData("{Binding Path='unterminated}")]
    [InlineData("{Binding X=1, X=2}")]
    [InlineData("{Binding Path=Name")]
    [InlineData("{}literal")]
    public void Identity_source_mapping_matches_direct_parsing_including_diagnostics(string text)
    {
        var source = "prefix:" + text + ":suffix";
        var span = new TextSpan(7, text.Length);
        var directDiagnostics = new List<XamlDiagnostic>();
        var sourceDiagnostics = new List<XamlDiagnostic>();
        var direct = MarkupExtensionParser.Parse(text, span, directDiagnostics.Add);
        var mapped = MarkupExtensionParser.ParseAtSource(text, span, source, sourceDiagnostics.Add);
        Assert.Equal(directDiagnostics, sourceDiagnostics);
        if (direct == null) { Assert.Null(mapped); return; }
        Assert.NotNull(mapped);
        Assert.Equal(direct.Name, mapped.Name);
        Assert.Equal(direct.Span, mapped.Span);
        Assert.Equal(direct.NameSpan, mapped.NameSpan);
        Assert.Equal(direct.Arguments.ToArray(), mapped.Arguments.ToArray());
    }

    [Fact]
    public void Encoded_markup_preserves_raw_entity_boundaries()
    {
        const string source = "<Root Value='{Binding Path=&quot;A&amp;B&quot;}'/>";
        var attribute = XamlSyntaxTree.Parse(source).Root!.Attributes[0];
        var diagnostics = new List<XamlDiagnostic>();
        var markup = MarkupExtensionParser.ParseAtSource(attribute.Value, attribute.ValueSpan, source, diagnostics.Add)!;
        Assert.Empty(diagnostics);
        var argument = Assert.Single(markup.Arguments);
        Assert.Equal("A&B", argument.Value);
        var span = argument.ValueSpan!.Value;
        Assert.Equal("A&amp;B", source.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Scanning_keeps_xml_whitespace_and_quoted_recovery_rules()
    {
        const string source = "<Root A='one\t&amp;\r\ntwo' B=\"three\"><Child/>text</Root>";
        var syntax = XamlSyntaxTree.Parse(source);
        Assert.Empty(syntax.Diagnostics);
        Assert.Equal("one & two", syntax.Root!.Attributes[0].Value);
        Assert.Equal("three", syntax.Root.Attributes[1].Value);
        Assert.Equal("text", Assert.IsType<XamlTextSyntax>(syntax.Root.Children[1]).Value);
        Assert.Contains(XamlSyntaxTree.Parse("<Root A='broken<Child/></Root>").Diagnostics, d => d.Code == "XG0005");
    }
}
