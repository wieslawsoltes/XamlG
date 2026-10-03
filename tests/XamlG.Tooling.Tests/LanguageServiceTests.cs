using XamlG.Syntax;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class LanguageServiceTests
{
    private const string Source = "<View xmlns='clr-namespace:Model' Text='hello' Enabled='True'/>";

    [Fact]
    public void EveryHostReceivesTheSameCachedAnalysis()
    {
        var session = ToolingFixture.Create();
        var tree = XamlSyntaxTree.Parse(Source, "View.xaml");
        var first = session.Analyze(tree);
        Assert.True(first.Output.Success, string.Join(";", first.Output.Diagnostics));
        Assert.Same(first, session.Analyze(tree));
        session.ClearCache();
        Assert.NotSame(first, session.Analyze(tree));
    }

    [Fact]
    public void HoverAndDefinitionUseTheBoundPropertySymbol()
    {
        var session = ToolingFixture.Create();
        var analysis = session.Analyze(XamlSyntaxTree.Parse(Source));
        var language = new XamlLanguageService(session);
        var position = Source.IndexOf("Text=", StringComparison.Ordinal);
        Assert.Contains("Text", language.GetHover(analysis, position)!.Signature);
        Assert.Equal("Model.cs", Assert.Single(language.GetDefinitions(analysis, position)).Path);
        Assert.Single(language.GetReferences(analysis, position));
    }

    [Fact]
    public void CompletionUsesNamespaceAndAvailableMembers()
    {
        var session = ToolingFixture.Create();
        var analysis = session.Analyze(XamlSyntaxTree.Parse(Source));
        var language = new XamlLanguageService(session);
        Assert.Contains(language.GetCompletions(analysis, 3, XamlCompletionKind.Element), c => c.Label == "View");
        var members = language.GetCompletions(analysis, 3, XamlCompletionKind.Attribute);
        Assert.Contains(members, c => c.Label == "Alignment");
        Assert.DoesNotContain(members, c => c.Label == "Text");
        Assert.Equal(2, language.GetCompletions(analysis, Source.IndexOf("True", StringComparison.Ordinal), XamlCompletionKind.Value).Length);
    }

    [Fact]
    public void InspectionContainsSyntaxAndTypedAssignments()
    {
        var session = ToolingFixture.Create();
        var analysis = session.Analyze(XamlSyntaxTree.Parse(Source));
        var syntax = Assert.Single(XamlInspector.Syntax(analysis.Syntax));
        Assert.Contains(syntax.Children, c => c.Kind == "Attribute" && c.Label.StartsWith("Text"));
        var bound = XamlInspector.Bound(analysis.Document)!;
        Assert.Equal("Model.View", bound.TypeName);
        Assert.Contains(bound.Children, c => c.Label == "Text");
    }

    [Fact]
    public void CanceledAnalysisIsNotPublished()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var session = ToolingFixture.Create();
        Assert.Throws<OperationCanceledException>(() => session.Analyze(XamlSyntaxTree.Parse(Source), cancellation.Token));
    }
}
