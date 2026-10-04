using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp.Resources;
using XamlG.Runtime;
using XamlG.Syntax;
using XamlG.Tooling.Refactoring;
using Xunit;

namespace XamlG.Tooling.Tests;

internal sealed class FileMoveFixture
{
    private const string Model = "namespace Model { public class Item { public object Child {get;set;} public string Text {get;set;} public System.Collections.Generic.List<object> Children {get;} = new(); } public class Include { public string Source {get;set;} } }";
    public FileMoveFixture(params (string Path, string Body)[] documents)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("FileMoves", new[] { CSharpSyntaxTree.ParseText(Model, new CSharpParseOptions(LanguageVersion.Preview)) },
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var profile = XamlFrameworkProfile.Portable with
        {
            ObjectExpressionRules = ImmutableArray.Create<IXamlObjectExpressionRule>(new FileMoveResourceRule()),
            ResourceSourceMembers = ImmutableDictionary<string, string>.Empty.Add("Model.Include", "Source")
        };
        var inputs = documents.Select(d => new XamlProjectDocument(XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model'>" + d.Body + "</Item>", Physical(d.Path)), d.Path));
        Compiler = new(compilation, profile, projectDocuments: inputs);
        Analyses = Compiler.AnalyzeWorkspace(Array.Empty<XamlSyntaxTree>());
        Assert.All(Analyses, a => Assert.True(a.Output.Success, string.Join("\n", a.Output.Diagnostics)));
    }
    public XamlCompilationSession Compiler { get; }
    public ImmutableArray<XamlAnalysis> Analyses { get; }
    public static string Physical(string path) => "/project/" + path;
    public static XamlDocumentMove Move(string before, string after) => new(Physical(before), Physical(after), after);
    public XamlFileRenamePlan Plan(params XamlDocumentMove[] moves) => new XamlFileRenameService(Compiler).Plan(moves, Analyses);
    public static string Child(string source) => "<Item.Child><Include Source='" + source + "'/></Item.Child>";
    public static string Text(XamlFileRenamePlan plan, string logicalPath) => plan.UpdatedDocuments.Single(d => d.LogicalPath == logicalPath).Syntax.Text;
    public void AssertEmits(XamlFileRenamePlan plan)
    {
        var result = new XamlProjectCompiler().Compile(plan.UpdatedDocuments, Compiler.Types.Compilation, Compiler.Profile);
        var generated = result.Documents.Select(d => CSharpSyntaxTree.ParseText(d.Output.Source, new CSharpParseOptions(LanguageVersion.Preview), d.Output.HintName));
        using var image = new MemoryStream();
        var emitted = Compiler.Types.Compilation.AddSyntaxTrees(generated).Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
    }
}
