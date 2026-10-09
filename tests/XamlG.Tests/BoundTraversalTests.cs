using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class BoundTraversalTests
{
    private static readonly Func<BoundObject, bool, IEnumerable<BoundObject>> Walk =
        typeof(CSharpEmitter).Assembly.GetType("XamlG.CSharp.BoundTraversal")!
            .GetMethod("Objects", [typeof(BoundObject), typeof(bool)])!
            .CreateDelegate<Func<BoundObject, bool, IEnumerable<BoundObject>>>();

    [Fact]
    public void VisitsConstructorArgumentsAssignmentsDescriptorsAndPostCallsInEvaluationOrder()
    {
        var fixture = new Fixture();
        var root = fixture.Root with
        {
            Arguments = [fixture.Value("constructor-1"), fixture.Value("constructor-2")],
            Assignments = [
                new BoundSetAssignment(fixture.Member, fixture.Value("set"), default),
                new BoundAdaptedSetAssignment(fixture.Member, fixture.Value("adapted"), [], fixture.Method, default),
                new BoundDynamicSetAssignment(fixture.Member, fixture.Value("dynamic"), [], default),
                new BoundEventAssignment(fixture.Member, "Handler", fixture.Method, default),
                new BoundEventAssignment(fixture.Member, "", null, default) { Value = fixture.Value("event") },
                new BoundAddAssignment(null, fixture.Method, [fixture.Value("add-1"), fixture.Value("add-2")], default)
                    { PostCall = new(fixture.Method, [], [fixture.Value("add-post")]) },
                new BoundCallAssignment(fixture.Method, [fixture.Value("call-1"), fixture.Value("call-2")], false, default)
                {
                    TargetDescriptor = fixture.Value("descriptor"),
                    PostCall = new(fixture.Method, [], [fixture.Value("call-post")])
                },
                new BoundCallAssignment(fixture.Method, [fixture.Value("plain-call")], false, default),
                new BoundRawAssignment("", default)
            ]
        };
        Assert.Equal(new[] { "root", "constructor-1", "constructor-2", "set", "adapted", "dynamic", "event",
            "add-1", "add-2", "add-post", "descriptor", "call-1", "call-2", "call-post", "plain-call" },
            Walk(root, true).Select(value => value.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChoiceVisitsItsReceiverOnceThenEveryBranchAndOptionalDeferredContent(bool includeDeferred)
    {
        var fixture = new Fixture();
        var extension = fixture.Node("extension") with
        {
            Arguments = [fixture.Value("extension-argument")],
            Assignments = [new BoundSetAssignment(fixture.Member, fixture.Value("extension-property"), default)]
        };
        var root = fixture.Root with
        {
            Arguments = [new BoundChoiceExpression(extension,
                [new(fixture.Method, fixture.Value("option-1"), fixture.Value("value-1")),
                 new(fixture.Method, fixture.Value("option-2"),
                     new BoundDeferredExpression(fixture.Value("deferred"), fixture.Root.Type, default))],
                fixture.Value("fallback"), fixture.Root.Type, default),
                new BoundChoiceExpression(fixture.Node("empty-choice"), [], null, fixture.Root.Type, default)]
        };
        var expected = new List<string> { "root", "extension", "extension-argument", "extension-property", "option-1", "value-1", "option-2" };
        if (includeDeferred) expected.Add("deferred");
        expected.AddRange(["fallback", "empty-choice"]);
        Assert.Equal(expected, Walk(root, includeDeferred).Select(value => value.Key));
    }

    [Fact]
    public void WalksEveryCompositeExpressionWithoutChangingChildOrderOrDeduplicatingSharedObjects()
    {
        var f = new Fixture();
        var shared = f.Value("shared");
        var root = f.Root with
        {
            Arguments = [
                new BoundMarkupExpression(f.Node("markup") with { Arguments = [f.Value("markup-argument")] }, f.Method, f.Root.Type, default),
                new BoundCastExpression(f.Value("cast"), f.Root.Type, default),
                new BoundArrayExpression([f.Value("array-1"), f.Value("array-2")], f.ArrayType, default),
                new BoundNewExpression(f.Root.Constructor!, [f.Value("new-argument")], default)
                    { Initializers = [new(f.Property, f.Value("initializer-1")), new(f.Property, f.Value("initializer-2"))] },
                new BoundBuilderExpression(f.Value("builder"),
                    [new(f.Method, [f.Value("builder-1"), f.Value("builder-2")]), new(f.Method, [f.Value("builder-3")])], f.Method, default),
                new BoundScopedInitializationExpression(f.Value("scope"), f.Method,
                    [new(f.Method, [f.Value("scope-1"), f.Value("scope-2")])], null, default),
                new BoundCollectionExpression(f.Root.Constructor!, f.Method, f.Property, [f.Value("collection-1"), f.Value("collection-2")], default),
                new BoundCachedExpression("cache", f.Value("cached"), default),
                new BoundValueConverterExpression(f.Value("converted"), f.Root.Type, f.Root.Type, default),
                new BoundCallExpression(f.Method, f.Value("receiver"), [f.Value("argument-1"), f.Value("argument-2")], default),
                new BoundCallExpression(f.Method, null, [f.Value("static-argument")], default),
                new BoundLambdaExpression(f.Root.Type, [], f.Value("lambda-body"), true, default),
                new BoundPropertyAccessExpression(f.Value("property-receiver"), f.Property, [f.Value("index-1"), f.Value("index-2")], default),
                new BoundFieldAccessExpression(f.Value("field-receiver"), f.Field, default),
                new BoundAssignmentExpression(f.Value("target"), f.Value("assigned"), default),
                new BoundMethodGroupExpression(f.Method, f.Value("method-receiver"), f.Root.Type, default),
                new BoundMethodGroupExpression(f.Method, null, f.Root.Type, default),
                new BoundConstantExpression(null, f.Root.Type, default), shared, shared
            ]
        };
        var visited = Walk(root, true).ToArray();
        Assert.Equal(new[] { "root", "markup", "markup-argument", "cast", "array-1", "array-2", "new-argument",
            "initializer-1", "initializer-2", "builder", "builder-1", "builder-2", "builder-3", "scope", "scope-1", "scope-2",
            "collection-1", "collection-2", "cached", "converted", "receiver", "argument-1", "argument-2", "static-argument",
            "lambda-body", "property-receiver", "index-1", "index-2", "field-receiver", "target", "assigned", "method-receiver", "shared", "shared" },
            visited.Select(value => value.Key));
        Assert.Same(visited[^2], visited[^1]);
    }

    [Fact]
    public void DeepObjectAndExpressionChainsDoNotUseTheCallStack()
    {
        var f = new Fixture();
        var node = f.Node("leaf");
        const int depth = 20_000;
        for (var i = depth - 1; i >= 0; i--)
            node = f.Node(i.ToString()) with
            {
                Arguments = [new BoundCastExpression(new BoundObjectExpression(node), f.Root.Type, default)]
            };
        var visited = Walk(node, true).ToArray();
        Assert.Equal(depth + 1, visited.Length);
        Assert.Equal("0", visited[0].Key);
        Assert.Equal("leaf", visited[^1].Key);
    }

    private sealed class Fixture
    {
        public BoundObject Root { get; }
        public BoundMember Member { get; }
        public IPropertySymbol Property { get; }
        public IFieldSymbol Field { get; }
        public IMethodSymbol Method { get; }
        public IArrayTypeSymbol ArrayType { get; }
        public Fixture()
        {
            var compilation = CompilationFactory.Create("""
                namespace Traversal;
                public class Node {
                    public Node Child { get; set; }
                    public Node Field;
                    public Node Call(Node first, Node second) => first;
                }
                """);
            var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Node xmlns='clr-namespace:Traversal'/>"), compilation);
            Assert.True(document.Success, string.Join("\n", document.Diagnostics));
            Root = document.Root! with { Key = "root" };
            Property = (IPropertySymbol)Root.Type.GetMembers("Child").Single();
            Member = new(Property.Name, BoundMemberKind.Property, Property, Property.Type, Property.GetMethod, Property.SetMethod, default);
            Field = (IFieldSymbol)Root.Type.GetMembers("Field").Single();
            Method = (IMethodSymbol)Root.Type.GetMembers("Call").Single();
            ArrayType = compilation.CreateArrayTypeSymbol(Root.Type);
        }
        public BoundObject Node(string key) => Root with { Key = key, IsRoot = false };
        public BoundObjectExpression Value(string key) => new(Node(key));
    }
}
