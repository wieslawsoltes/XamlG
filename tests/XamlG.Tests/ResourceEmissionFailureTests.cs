using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.CSharp.Resources;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ResourceEmissionFailureTests
{
    [Fact]
    public void ABackendFailureSuppressesCallersAndRecoveryReusesTheirRawOutput()
    {
        var compilation = CompilationFactory.Create("namespace Model { public class Root { public object Child {get;set;} } public interface ITarget { object TargetObject { get; } object TargetProperty { get; } } }");
        if (!compilation.References.OfType<PortableExecutableReference>().Any(reference => reference.FilePath == typeof(XamlRuntimeContext).Assembly.Location))
            compilation = compilation.AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
        var profile = XamlFrameworkProfile.Portable with
        {
            ObjectExpressionRules = ImmutableArray.Create<IXamlObjectExpressionRule>(new Rule()),
            Runtime = new XamlRuntimeConfiguration
            { Services = ImmutableArray.Create(new XamlServiceMapping("Model.ITarget", XamlServiceKind.ProvideValueTarget)) }
        };
        static XamlProjectDocument Document(string path, string body) => new(XamlSyntaxTree.Parse("<Root xmlns='clr-namespace:Model'>" + body + "</Root>", path), path);
        var root = Document("Root.xaml", "<Root.Child><Include Source='Middle.xaml'/></Root.Child>");
        var middle = Document("Middle.xaml", "<Root.Child><Include Source='Leaf.xaml'/></Root.Child>");
        var leaf = Document("Leaf.xaml", "");
        var independent = Document("ZIndependent.xaml", "<Root.Child><Root/></Root.Child>");
        var compiler = new XamlProjectCompiler();
        var success = compiler.Compile(new[] { root, middle, leaf, independent }, compilation, profile);
        Assert.True(success.Success);
        var broken = Document("Leaf.xaml", "<Root.Child><BackendFailure/></Root.Child>");
        var failure = compiler.Compile(new[] { root, middle, broken, independent }, compilation, profile);
        Assert.False(failure.Success);
        foreach (var result in failure.Documents.Where(document => document.Input.LogicalPath is "Root.xaml" or "Middle.xaml"))
        {
            Assert.Contains(result.Output.Diagnostics, diagnostic => diagnostic.Code == "XG3305");
            Assert.Empty(result.Output.Source);
            Assert.False(result.Document.Success);
        }
        var survivingOutput = failure.Documents.Single(document => document.Input.LogicalPath == "ZIndependent.xaml").Output;
        Assert.True(survivingOutput.Success);
        // Failed documents and their suppressed callers cannot own service or property
        // helpers that are still required by a valid, independently emitted document.
        using var image = new MemoryStream();
        var compiled = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(survivingOutput.Source,
            new CSharpParseOptions(LanguageVersion.Preview))).Emit(image);
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
        var recovered = compiler.Compile(new[] { root, middle, leaf, independent }, compilation, profile);
        Assert.True(recovered.Success);
        foreach (var path in new[] { "Root.xaml", "Middle.xaml" })
            Assert.Same(success.Documents.Single(d => d.Input.LogicalPath == path).Output, recovered.Documents.Single(d => d.Input.LogicalPath == path).Output);
    }

    private sealed record UnknownExpression(ITypeSymbol ValueType, TextSpan Span) : BoundExpression(ValueType, Span);
    private sealed class Rule : IXamlObjectExpressionRule
    {
        public bool TryBind(BindingContext context, XamlElementSyntax syntax, ITypeSymbol targetType,
            NamespaceScope parentScope, int nameScope, out BoundExpression? expression)
        {
            expression = null;
            if (syntax.Name == "BackendFailure") { expression = new UnknownExpression(targetType, syntax.Span); return true; }
            if (syntax.Name != "Include") return false;
            var attribute = syntax.Attributes.Single(a => a.Name == "Source");
            var lookup = context.Options.Resources!.Resolve(context.Options.ResourceUri, attribute.Value);
            if (lookup.Success) expression = new BoundResourceExpression(lookup.Resource!, attribute.ValueSpan);
            else context.Report("TEST0001", lookup.Error!, attribute.ValueSpan);
            return true;
        }
    }
}
