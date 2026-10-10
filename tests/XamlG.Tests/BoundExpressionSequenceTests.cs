using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class BoundExpressionSequenceTests
{
    [Fact]
    public void Every_assignment_shape_preserves_descriptor_argument_and_post_call_order()
    {
        var f = new Fixture();
        var a = f.Value(1); var b = f.Value(2); var c = f.Value(3); var d = f.Value(4);
        var cases = new (BoundAssignment Assignment, BoundExpression[] Expected)[]
        {
            (new BoundSetAssignment(f.Member, a, default), [a]),
            (new BoundAdaptedSetAssignment(f.Member, a, [], f.Method, default), [a]),
            (new BoundDynamicSetAssignment(f.Member, a, [], default), [a]),
            (new BoundEventAssignment(f.Member, "Handler", null, default), []),
            (new BoundEventAssignment(f.Member, "", null, default) { Value = a }, [a]),
            (new BoundAddAssignment(null, f.Method, [a, b], default) { PostCall = new(f.Method, [], [c, d]) }, [a, b, c, d]),
            (new BoundAddAssignment(null, f.Method, [], default), []),
            (new BoundCallAssignment(f.Method, [b, c], false, default) { TargetDescriptor = a, PostCall = new(f.Method, [], [d]) }, [a, b, c, d]),
            (new BoundCallAssignment(f.Method, [a, a], false, default), [a, a]),
            (new BoundRawAssignment("", default), [])
        };
        foreach (var (assignment, expected) in cases)
        {
            Same(expected, BoundTraversal.Expressions(assignment));
            Same(expected, BoundTraversal.Expressions(assignment));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_expression_shape_preserves_immediate_edge_order_and_deferred_boundary(bool deferred)
    {
        var f = new Fixture();
        var a = f.Value(1); var b = f.Value(2); var c = f.Value(3); var d = f.Value(4); var e = f.Value(5); var z = f.Value(6);
        var owner = f.Root with { Arguments = [a], Assignments = [new BoundSetAssignment(f.Member, b, default)] };
        var calls = ImmutableArray.Create(new BoundBuilderCall(f.Method, []), new(f.Method, [b, c]), new(f.Method, [d]));
        var cases = new (BoundExpression Expression, BoundExpression[] Expected)[]
        {
            (a, []),
            (new BoundObjectExpression(owner), [a, b]),
            (new BoundMarkupExpression(owner, f.Method, f.Root.Type, default), [a, b]),
            (new BoundChoiceExpression(owner, [new(f.Method, c, d), new(f.Method, e, a)], z, f.Root.Type, default), [a, b, c, d, e, a, z]),
            (new BoundChoiceExpression(f.Root, [], null, f.Root.Type, default), []),
            (new BoundCastExpression(a, f.Root.Type, default), [a]),
            (new BoundArrayExpression([a, b, a], f.Array, default), [a, b, a]),
            (new BoundArrayExpression([], f.Array, default), []),
            (new BoundNewExpression(f.Root.Constructor!, [a, b], default) { Initializers = [new(f.Property, c), new(f.Property, d)] }, [a, b, c, d]),
            (new BoundNewExpression(f.Root.Constructor!, [], default), []),
            (new BoundBuilderExpression(a, calls, f.Method, default), [a, b, c, d]),
            (new BoundBuilderExpression(a, [], f.Method, default), [a]),
            (new BoundScopedInitializationExpression(a, f.Method, calls, null, default), [a, b, c, d]),
            (new BoundScopedInitializationExpression(a, f.Method, [], null, default), [a]),
            (new BoundCollectionExpression(f.Root.Constructor!, f.Method, f.Property, [a, b], default), [a, b]),
            (new BoundCachedExpression("key", a, default), [a]),
            (new BoundValueConverterExpression(a, f.Root.Type, f.Root.Type, default), [a]),
            (new BoundCallExpression(f.Method, a, [b, c], default), [a, b, c]),
            (new BoundCallExpression(f.Method, null, [a, b], default), [a, b]),
            (new BoundLambdaExpression(f.Root.Type, [], a, false, default), [a]),
            (new BoundPropertyAccessExpression(a, f.Property, [b, c], default), [a, b, c]),
            (new BoundFieldAccessExpression(a, f.Field, default), [a]),
            (new BoundAssignmentExpression(a, b, default), [a, b]),
            (new BoundMethodGroupExpression(f.Method, a, f.Root.Type, default), [a]),
            (new BoundMethodGroupExpression(f.Method, null, f.Root.Type, default), []),
            (new BoundDeferredExpression(a, f.Root.Type, default), deferred ? [a] : [])
        };
        foreach (var (expression, expected) in cases)
        {
            Same(expected, BoundTraversal.Children(expression, deferred));
            Same(expected, BoundTraversal.Children(expression, deferred));
        }
    }

    [Fact]
    public void Enumerators_are_independent_and_dispose_compound_remainders_on_early_exit()
    {
        var a = new BoundConstantExpression(1, null, default);
        var disposed = 0;
        IEnumerable<BoundExpression> Tail()
        {
            try { yield return a; yield return a; }
            finally { disposed++; }
        }
        var sequence = new BoundExpressionSequence(a, [a], [], null, Tail());
        foreach (var value in sequence) { Assert.Same(a, value); break; }
        Assert.Equal(0, disposed); // A remainder not started must not be enumerated just to dispose it.
        var left = sequence.GetEnumerator(); var right = sequence.GetEnumerator();
        try
        {
            Assert.True(left.MoveNext()); Assert.True(left.MoveNext()); Assert.True(left.MoveNext());
            Assert.True(right.MoveNext());
            Assert.Same(left.Current, right.Current);
        }
        finally { left.Dispose(); right.Dispose(); }
        Assert.Equal(1, disposed);
        Same([a, a, a, a], sequence);
        Assert.Equal(2, disposed);
        Assert.False(left.MoveNext());
        Same([], default(BoundExpressionSequence));
    }

    [Fact]
    public void Common_call_edges_allocate_nothing_during_direct_enumeration()
    {
        var f = new Fixture();
        var a = f.Value(1);
        var call = new BoundCallExpression(f.Method, a, [a, a], default);
        for (var i = 0; i < 8192; i++) Count(call);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var count = 0;
        for (var i = 0; i < 8192; i++) count += Count(call);
        var delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(24576, count);
        Assert.Equal(0, delta);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Count(BoundExpression expression)
    {
        var count = 0;
        foreach (var child in BoundTraversal.Children(expression, true)) count += child.Span.Length;
        return count;
    }

    private static void Same(BoundExpression[] expected, IEnumerable<BoundExpression> actual)
    {
        var values = actual.ToArray();
        Assert.Equal(expected.Length, values.Length);
        for (var i = 0; i < values.Length; i++) Assert.Same(expected[i], values[i]);
    }

    private sealed class Fixture
    {
        public BoundObject Root { get; }
        public BoundMember Member { get; }
        public IMethodSymbol Method { get; }
        public IPropertySymbol Property { get; }
        public IFieldSymbol Field { get; }
        public IArrayTypeSymbol Array { get; }
        public Fixture()
        {
            var compilation = CompilationFactory.Create("namespace Edges { public class Node { public Node Child { get; set; } public Node Field; public Node Call() => this; } }");
            var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Node xmlns='clr-namespace:Edges'/>"), compilation);
            Assert.True(document.Success);
            Root = document.Root!;
            Method = (IMethodSymbol)Root.Type.GetMembers("Call").Single();
            Property = (IPropertySymbol)Root.Type.GetMembers("Child").Single();
            Field = (IFieldSymbol)Root.Type.GetMembers("Field").Single();
            Member = new(Property.Name, BoundMemberKind.Property, Property, Property.Type, Property.GetMethod, Property.SetMethod, default);
            Array = compilation.CreateArrayTypeSymbol(Root.Type);
        }
        public BoundExpression Value(int id) => new BoundConstantExpression(id, Root.Type, new(id, 1));
    }
}
