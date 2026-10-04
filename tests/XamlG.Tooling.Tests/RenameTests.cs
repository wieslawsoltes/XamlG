using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Runtime;
using XamlG.Syntax;
using XamlG.Tooling.Refactoring;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class RenameTests
{
    private const string Ns = "xmlns='clr-namespace:Model' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
    private const string Model = """
        using System;
        using System.Collections.Generic;
        using XamlG.Runtime;
        namespace Model {
            public class Panel { [Content] public List<object> Children {get;} = new(); }
            public class Item { public string Name {get;set;} public object Target {get;set;} public string Text {get;set;} }
            public class Template { [Content, DeferredContent] public Func<IServiceProvider,object> Content {get;set;} }
        }
        """;
    internal static XamlCompilationSession Compiler(string code = "")
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct().Select(p => MetadataReference.CreateFromFile(p));
        return new(CSharpCompilation.Create("Rename", new[] { CSharpSyntaxTree.ParseText(Model + code, new CSharpParseOptions(LanguageVersion.Preview), "Code.cs") },
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
    }
    private static (XamlCompilationSession Compiler, XamlAnalysis Analysis) Analyze(string body, string code = "", string root = "Panel", string attributes = "")
    {
        var compiler = Compiler(code);
        var syntax = XamlSyntaxTree.Parse("<" + root + " " + Ns + attributes + ">" + body + "</" + root + ">", "View.xaml", version: 7);
        var analysis = compiler.Analyze(syntax);
        Assert.True(analysis.Output.Success, string.Join("\n", analysis.Output.Diagnostics));
        return (compiler, analysis);
    }
    [Fact]
    public void RenamesDecodedReferencesWithoutChangingCommentsAndLiteralStrings()
    {
        var (compiler, analysis) = Analyze("<Item x:Name='target'/><Item Target=\"{x:Reference Name='t&#97;rget'}\" Text='target'/><!--target-->");
        var plan = new XamlRenameService(compiler).Rename(analysis, analysis.Syntax.Text.IndexOf("t&#97;", StringComparison.Ordinal), "renamed");
        var change = Assert.Single(plan.Documents);
        Assert.Equal(7, change.Version);
        Assert.Equal(2, change.Changes.Length);
        var updated = analysis.Syntax.WithChanges(change.Changes, 7);
        Assert.Contains("x:Name='renamed'", updated.Text);
        Assert.Contains("Name='renamed'", updated.Text);
        Assert.Contains("Text='target'/><!--target-->", updated.Text);
        Assert.True(compiler.Analyze(updated).Output.Success);
    }
    [Fact]
    public void NamesInDifferentDeferredScopesAreNotRenamedTogether()
    {
        var (compiler, analysis) = Analyze("<Item x:Name='same'/><Template><Panel><Item x:Name='same'/><Item Target='{x:Reference same}'/></Panel></Template><Item Target='{x:Reference same}'/>");
        var plan = new XamlRenameService(compiler).Rename(analysis, analysis.Syntax.Text.IndexOf("same", StringComparison.Ordinal), "outer");
        var updated = analysis.Syntax.WithChanges(Assert.Single(plan.Documents).Changes, 7);
        Assert.Contains("<Template><Panel><Item x:Name='same'/><Item Target='{x:Reference same}'/>", updated.Text);
        Assert.Contains("<Item Target='{x:Reference outer}'/>", updated.Text);
    }
    [Fact]
    public void IgnorableDesignReferencesAreNotTreatedAsRuntimeReferences()
    {
        var (compiler, analysis) = Analyze("<Item x:Name='target'/><Item d:Preview='{x:Reference target}'/>",
            attributes: " xmlns:d='urn:design' xmlns:mc='http://schemas.openxmlformats.org/markup-compatibility/2006' mc:Ignorable='d'");
        var plan = new XamlRenameService(compiler).Rename(analysis, analysis.Syntax.Text.IndexOf("target", StringComparison.Ordinal), "renamed");
        Assert.Single(Assert.Single(plan.Documents).Changes);
    }
    [Fact]
    public void CollisionsAndInvalidIdentifiersAreRejected()
    {
        var (compiler, analysis) = Analyze("<Item x:Name='first'/><Item x:Name='second'/>");
        var service = new XamlRenameService(compiler); var position = analysis.Syntax.Text.IndexOf("first", StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => service.Rename(analysis, position, "second"));
        Assert.Throws<ArgumentException>(() => service.Rename(analysis, position, "class"));
        Assert.Throws<ArgumentException>(() => service.Rename(analysis, position, "a.b"));
    }
    [Fact]
    public void GeneratedFieldUsesAreResolvedByRoslynAcrossCodeBehind()
    {
        const string code = """
            namespace Model { public partial class View : Panel {
                public View() { InitializeComponent(); }
                public string Read() => target.Text + nameof(target) + "target";
                public string Other() { var target = "local"; return target; }
            } }
            """;
        var (compiler, analysis) = Analyze("<Item x:Name='target'/>", code, "Panel", " x:Class='Model.View'");
        var plan = new XamlRenameService(compiler).Rename(analysis, analysis.Syntax.Text.IndexOf("target", StringComparison.Ordinal), "renamed");
        Assert.Equal(2, plan.Documents.Length);
        var codeEdits = plan.Documents.Single(d => d.Path == "Code.cs");
        Assert.Equal(2, codeEdits.Changes.Length);
        var text = Microsoft.CodeAnalysis.Text.SourceText.From(codeEdits.OriginalText).WithChanges(codeEdits.Changes.Select(c =>
            new Microsoft.CodeAnalysis.Text.TextChange(new(c.Span.Start, c.Span.Length), c.NewText))).ToString();
        Assert.Contains("renamed.Text + nameof(renamed) + \"target\"", text);
        Assert.Contains("var target = \"local\"; return target;", text);
    }
    [Fact]
    public void CodeBehindLocalCaptureIsRejectedBeforeAnyEditIsReturned()
    {
        const string code = "namespace Model { public partial class View : Panel { public string Read() { var renamed = new Item(); return target.Text; } } }";
        var (compiler, analysis) = Analyze("<Item x:Name='target'/>", code, attributes: " x:Class='Model.View'");
        Assert.Throws<InvalidOperationException>(() => new XamlRenameService(compiler).Rename(analysis, analysis.Syntax.Text.IndexOf("target", StringComparison.Ordinal), "renamed"));
    }
}
