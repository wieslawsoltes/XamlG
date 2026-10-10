using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class NamespaceDirectiveFastPathTests
{
    [Fact]
    public void Filtered_lookup_matches_expansion_for_aliases_empty_names_and_redeclarations()
    {
        var root = XamlSyntaxTree.Parse("<Root xmlns:x='" + XamlNames.Language2006 + "' xmlns:y='" + XamlNames.Language2009 +
            "' xmlns:p='urn:other' x:Name='first' y:Name='second' p:Name='ignored' Name='unqualified' x:='empty'>" +
            "<Child xmlns:x='urn:other' x:Name='hidden' y:Name='child'/></Root>").Root!;
        var parent = NamespaceScope.Empty.Push(root);
        var child = root.Children.OfType<XamlElementSyntax>().Single();
        foreach (var (element, scope) in new[] { (root, parent), (child, parent.Push(child)) })
            foreach (var local in new[] { "Name", "Class", "TypeArguments", "", "p:Name", "name", "NameLonger" })
            {
                var expected = element.Attributes.FirstOrDefault(attribute =>
                {
                    var expanded = scope.Expand(attribute.Name, true);
                    return expanded.Namespace != null && XamlNames.IsLanguage(expanded.Namespace) && expanded.LocalName == local;
                });
                Assert.Same(expected, scope.Directive(element, local));
            }
    }

    [Fact]
    public void Xml_space_and_ignorable_aliases_remain_scoped_and_order_independent()
    {
        var root = XamlSyntaxTree.Parse("<Root s:space='preserve' c:Ignorable='p\tq' xmlns:s='" + XamlNames.Xml +
            "' xmlns:c='" + XamlNames.Compatibility + "' xmlns:p='urn:p' xmlns:q='urn:q'>" +
            "<Child s:space='default' xmlns:p='urn:new' c:Ignorable='p'/></Root>").Root!;
        var parent = NamespaceScope.Empty.Push(root);
        var child = parent.Push(root.Children.OfType<XamlElementSyntax>().Single());
        Assert.True(parent.PreserveSpace); Assert.False(child.PreserveSpace);
        Assert.Contains("urn:p", parent.IgnoredNamespaces); Assert.Contains("urn:q", parent.IgnoredNamespaces);
        Assert.DoesNotContain("urn:new", parent.IgnoredNamespaces); Assert.Contains("urn:new", child.IgnoredNamespaces);
        Assert.Contains("urn:p", child.IgnoredNamespaces);
    }
}
