using Microsoft.CodeAnalysis.CSharp;
using XamlG.Syntax;
using XamlG.Tooling.Formatting;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class SignificantFormattingTests
{
    [Theory]
    [InlineData("<Owner.Inlines><Item/> <Item/></Owner.Inlines>")]
    [InlineData("<Item/> <Item/>")]
    public void SignificantCollectionsKeepTheirInterItemWhitespace(string content)
    {
        var original = RenameTests.Compiler();
        var code = CSharpSyntaxTree.ParseText("""
            namespace Model {
              [XamlG.Runtime.WhitespaceSignificantCollection] public class Spans : System.Collections.Generic.List<object> { }
              public class Owner { [XamlG.Runtime.Content] public Spans Inlines {get;} = new(); }
            }
            """, new CSharpParseOptions(LanguageVersion.Preview));
        var compiler = new XamlCompilationSession(original.Types.Compilation.AddSyntaxTrees(code));
        var syntax = XamlSyntaxTree.Parse("<Owner xmlns='clr-namespace:Model'>" + content + "</Owner>");
        var analysis = compiler.Analyze(syntax);
        Assert.True(analysis.Output.Success, string.Join("\n", analysis.Output.Diagnostics));
        var updated = syntax.WithChanges(XamlFormatter.Format(syntax, analysis: analysis), syntax.Version);
        Assert.Contains("<Item /> <Item />", updated.Text);
        Assert.True(compiler.Analyze(updated).Output.Success);
    }
}
