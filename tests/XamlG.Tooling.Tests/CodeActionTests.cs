using XamlG.Syntax;
using XamlG.Tooling.Refactoring;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class CodeActionTests
{
    [Fact]
    public void UniqueMemberTypoFixHasMinimalEditAndCompiles()
    {
        var compiler = RenameTests.Compiler();
        var syntax = XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model' Txet='keep &amp; preserve'/>", "View.xaml");
        var analysis = compiler.Analyze(syntax);
        Assert.False(analysis.Output.Success);
        var action = Assert.Single(new XamlCodeActionService(compiler).GetActions(analysis, new(syntax.Text.IndexOf("Txet", StringComparison.Ordinal), 4)).Where(a => a.Kind == "quickfix"));
        var edit = Assert.Single(action.Changes);
        Assert.Equal("Text", edit.NewText); Assert.Equal(4, edit.Span.Length);
        var updated = syntax.WithChanges(action.Changes, syntax.Version);
        Assert.Contains("'keep &amp; preserve'", updated.Text);
        Assert.True(compiler.Analyze(updated).Output.Success);
    }
    [Fact]
    public void EmptyElementRewritesDoNotDeleteTextOrComments()
    {
        var compiler = RenameTests.Compiler(); var actions = new XamlCodeActionService(compiler);
        var syntax = XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model'/>", "View.xaml");
        var expand = Assert.Single(actions.GetActions(compiler.Analyze(syntax), new(1, 0)).Where(a => a.Title.StartsWith("Expand", StringComparison.Ordinal)));
        var expanded = syntax.WithChanges(expand.Changes, syntax.Version);
        Assert.EndsWith("></Item>", expanded.Text);
        var collapse = Assert.Single(actions.GetActions(compiler.Analyze(expanded), new(1, 0)).Where(a => a.Title.StartsWith("Collapse", StringComparison.Ordinal)));
        Assert.EndsWith(" />", expanded.WithChanges(collapse.Changes, expanded.Version).Text);
        var comment = XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model'><!--keep--></Item>", "View.xaml");
        Assert.DoesNotContain(actions.GetActions(compiler.Analyze(comment), new(1, 0)), a => a.Title.StartsWith("Collapse", StringComparison.Ordinal));
    }
}
