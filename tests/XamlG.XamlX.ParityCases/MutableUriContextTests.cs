using System;
using System.Collections.Generic;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class MutableUriContextTests : CompilerTestBase
{
    private const string Ns = " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests'";

    [Theory]
    [InlineData("Uri=https://changed.example/path/", "https://changed.example/path/")]
    [InlineData("Clear=True", "null")]
    public void UriMutationsRemainVisibleToLaterAssignments(string argument, string expected)
    {
        var root = (MutableUriRoot)CompileAndRun("<MutableUriRoot" + Ns + " First='{MutableUri " + argument + "}' Second='{MutableUri}'/>");
        Assert.Equal(expected, root.First);
        Assert.Equal(expected, root.Second);
    }

    [Fact]
    public void UriMutationsAreSharedAcrossConstructionBranches()
    {
        var root = (MutableUriRoot)CompileAndRun("<MutableUriRoot" + Ns + "><MutableUriRoot First='{MutableUri Uri=https://changed.example/path/}'/><MutableUriRoot First='{MutableUri}'/></MutableUriRoot>");
        Assert.Equal("https://changed.example/path/", root.Children[0].First);
        Assert.Equal(root.Children[0].First, root.Children[1].First);
    }
}

public sealed class MutableUriRoot
{
    public string? First { get; set; }
    public string? Second { get; set; }
    [Content] public List<MutableUriRoot> Children { get; } = new();
}
public sealed class MutableUri
{
    public string? Uri { get; set; }
    public bool Clear { get; set; }
    public string ProvideValue(IServiceProvider services)
    {
        var context = (ITestUriContext)services.GetService(typeof(ITestUriContext))!;
        if (Uri != null) context.BaseUri = new Uri(Uri);
        if (Clear) context.BaseUri = null;
        return context.BaseUri?.OriginalString ?? "null";
    }
}
