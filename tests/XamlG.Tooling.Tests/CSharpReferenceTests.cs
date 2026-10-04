using XamlG.Tooling.Navigation;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class CSharpReferenceTests
{
    [Fact]
    public void ReferencesAreSymbolBasedAndHonorDeclarationInclusion()
    {
        var compiler = RenameTests.Compiler("""
            namespace Model { public class Usage {
                public string Read(Item item) => item.Text + nameof(Item.Text) + "Text";
                public string Other() { var Text = "local"; return Text; }
            } }
            """);
        var symbol = compiler.Types.Compilation.GetTypeByMetadataName("Model.Item")!.GetMembers("Text").Single();
        var service = new XamlCSharpReferenceService(compiler);
        var references = service.Find(symbol);
        Assert.Equal(2, references.Length); Assert.All(references, r => Assert.Equal("Code.cs", r.Path));
        Assert.Equal(3, service.Find(symbol, includeDeclaration: true).Length);
    }
    [Fact]
    public void ExplicitCodeBehindFieldDeclarationsAreOptionalReferenceResults()
    {
        var compiler = RenameTests.Compiler("namespace Model { public partial class View : Panel { private Item target; public string Read() => target.Text; } }");
        var syntax = XamlG.Syntax.XamlSyntaxTree.Parse("<Panel xmlns='clr-namespace:Model' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Class='Model.View'><Item x:Name='target'/></Panel>", "View.xaml");
        var analysis = compiler.Analyze(syntax);
        Assert.True(analysis.Output.Success, string.Join("\n", analysis.Output.Diagnostics));
        var service = new XamlCSharpReferenceService(compiler);
        Assert.Single(service.FindName(analysis, "target", new[] { analysis }));
        Assert.Equal(2, service.FindName(analysis, "target", new[] { analysis }, includeDeclaration: true).Length);
    }
}
