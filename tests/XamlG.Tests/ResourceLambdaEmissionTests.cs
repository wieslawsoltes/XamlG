using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.CSharp;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ResourceLambdaEmissionTests
{
    [Fact]
    public void AResourceFactoryCannotBeHoistedFromAnExpressionBodiedDelegate()
    {
        var compilation = CompilationFactory.Create("namespace Model { public class Root { public System.Func<object> Factory {get;set;} } }");
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse(
            "<Root xmlns='clr-namespace:Model' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Factory='{x:Null}'/>"), compilation);
        Assert.True(document.Success, string.Join("\n", document.Diagnostics));
        var root = document.Root!;
        var assignment = Assert.IsType<BoundSetAssignment>(Assert.Single(root.Assignments));
        var resource = new BoundResourceExpression(new XamlResourceDescriptor(
            "xamlg://model/Resource.xaml", root.Type, "Resource.xaml", "XamlG.Generated", null), root.Syntax.Span);
        var lambda = new BoundLambdaExpression((INamedTypeSymbol)assignment.Member.ValueType,
            ImmutableArray<BoundParameterExpression>.Empty, resource, true, root.Syntax.Span);
        var rewritten = document with
        {
            Root = root with { Assignments = ImmutableArray.Create<BoundAssignment>(assignment with { Value = lambda }) }
        };
        var output = new CSharpEmitter().Emit(rewritten);
        Assert.False(output.Success);
        Assert.Contains(output.Diagnostics, diagnostic => diagnostic.Code == "XG1200" && diagnostic.Message.Contains("resource-factory"));
        Assert.DoesNotContain("XamlResourceServices.Enter", output.Source);
    }
}
