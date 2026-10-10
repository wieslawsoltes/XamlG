using System.Collections.Immutable;
using System.Reflection;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.CSharp.Resources;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ResourceReferenceCacheTests
{
    [Fact]
    public void Reference_snapshots_keep_edge_order_and_follow_root_identity_not_diagnostics()
    {
        var compilation = CompilationFactory.Create("public class Item { public object Value {get;set;} }");
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:' Value='{x:Null}' xmlns:x='" + XamlNames.Language2006 + "'/>"), compilation);
        Assert.True(document.Success, string.Join("\n", document.Diagnostics));
        var root = document.Root!;
        var assignment = Assert.IsType<BoundSetAssignment>(Assert.Single(root.Assignments));
        var a = new BoundResourceExpression(new("xamlg://test/A.xaml", root.Type, "A.xaml", "Generated", null), new(1, 1));
        var b = new BoundResourceExpression(new("xamlg://test/B.xaml", root.Type, "B.xaml", "Generated", null), new(2, 1));
        var linked = document with { Root = root with { Assignments = ImmutableArray.Create<BoundAssignment>(assignment with { Value = a }, assignment with { Value = b }) } };
        var first = References(linked);
        Assert.Equal(new[] { b, a }, first.ToArray());
        var diagnosticsOnly = linked with { Diagnostics = linked.Diagnostics.Add(new("TEST", "error", new(0, 0))) };
        Assert.True(first.Equals(References(diagnosticsOnly)));
        var changed = linked with { Root = linked.Root! with { Assignments = ImmutableArray.Create<BoundAssignment>(assignment with { Value = a }) } };
        Assert.Equal(new[] { a }, References(changed).ToArray());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.IsType<OperationCanceledException>(Assert.Throws<TargetInvocationException>(() => References(linked, cancelled.Token)).InnerException);
        Assert.True(first.Equals(References(linked)));
    }

    private static ImmutableArray<BoundResourceExpression> References(BoundDocument document, CancellationToken token = default) =>
        (ImmutableArray<BoundResourceExpression>)typeof(XamlProjectCompiler).Assembly.GetType("XamlG.CSharp.Resources.XamlResourceGraph")!
            .GetMethod("References", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { document, token })!;
}
