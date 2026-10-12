using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class FrameDependencyAnalysisTests
{
    private const string Model = """
        namespace FrameCase;
        public class View { public object Value { get; set; } }
        public class Item {
            public static int Created;
            public Item() { Created++; }
            public Item(object value) { Created++; }
            public object Value { get; set; }
            public object Field;
            public static object Static;
            public object this[int index] => null;
            public object Call(object value) => value;
            public static object Parse(string value) => value;
            public void Step(object value) { }
            public Item End() => this;
            public System.IDisposable Begin() => null;
        }
        public delegate object Factory();
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_recognized_shape_matches_the_frozen_predicate(bool requiresFrame)
    {
        var compilation = CompilationFactory.Create(Model);
        var type = compilation.GetTypeByMetadataName("FrameCase.Item")!;
        var objectType = compilation.GetSpecialType(SpecialType.System_Object);
        var factory = compilation.GetTypeByMetadataName("FrameCase.Factory")!;
        var property = (IPropertySymbol)type.GetMembers("Value").Single();
        IMethodSymbol Method(string name) => (IMethodSymbol)type.GetMembers(name).Single();
        var constant = new BoundConstantExpression(null, objectType, default);
        BoundExpression input = requiresFrame ? new BoundServiceExpression(objectType, new(7, 3)) : constant;
        var list = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")!.Construct(objectType);
        var values = ImmutableArray.Create(constant, input, constant);
        var constructor = type.InstanceConstructors.Single(c => c.Parameters.IsEmpty);
        var calls = ImmutableArray.Create(new BoundBuilderCall(Method("Step"), []), new BoundBuilderCall(Method("Step"), values));
        BoundExpression[] expressions = [
            new BoundCastExpression(input, objectType, default), new BoundCachedExpression("key", input, default),
            new BoundNewExpression(constructor, values, default),
            new BoundNewExpression(constructor, [], default) { Initializers = [new(property, constant), new(property, input)] },
            new BoundBuilderExpression(input, [], Method("End"), default),
            new BoundBuilderExpression(constant, calls, Method("End"), default),
            new BoundScopedInitializationExpression(input, Method("Begin"), [], null, default),
            new BoundScopedInitializationExpression(constant, Method("Begin"), calls, property, default),
            new BoundArrayExpression(values, compilation.CreateArrayTypeSymbol(objectType), default),
            new BoundCollectionExpression(list.InstanceConstructors.Single(c => c.Parameters.IsEmpty),
                (IMethodSymbol)list.GetMembers("Add").Single(), (IPropertySymbol)list.GetMembers("Capacity").Single(), values, default),
            new BoundCallExpression(Method("Call"), input, [], default),
            new BoundCallExpression(Method("Call"), null, values, default),
            new BoundPropertyAccessExpression(input, property, [], default),
            new BoundPropertyAccessExpression(constant, property, values, default),
            new BoundFieldAccessExpression(input, (IFieldSymbol)type.GetMembers("Field").Single(), default),
            new BoundAssignmentExpression(input, constant, default), new BoundAssignmentExpression(constant, input, default),
            new BoundMethodGroupExpression(Method("Call"), input, factory, default),
            new BoundLambdaExpression(factory, [], input, false, default),
            new BoundLambdaExpression(factory, [], input, true, default)
        ];
        foreach (var expression in expressions)
        {
            Assert.Equal(requiresFrame, Previous(expression));
            Assert.Equal(Previous(expression), ValueEmitter.UsesFrame(expression));
            // Force the iterative path before the exact same shape.
            var pair = new BoundArrayExpression([constant, constant], compilation.CreateArrayTypeSymbol(objectType), default);
            var padding = Enumerable.Repeat<BoundExpression>(pair, 64).Append(expression).ToImmutableArray();
            var wide = new BoundArrayExpression(padding, compilation.CreateArrayTypeSymbol(objectType), default);
            Assert.Equal(Previous(wide), ValueEmitter.UsesFrame(wide));
        }
        foreach (var leaf in new BoundExpression[] { constant, new BoundParameterExpression("p", objectType, default),
            new BoundEnumExpression([], objectType, default), new BoundTypeExpression(type, objectType, default),
            new BoundMethodHandleExpression(Method("Call"), objectType, default),
            new BoundStaticExpression(type.GetMembers("Static").Single(), objectType, default),
            new BoundParseExpression("text", Method("Parse"), objectType, default),
            new BoundMethodGroupExpression(Method("Call"), null, factory, default) })
            Assert.False(ValueEmitter.UsesFrame(leaf));
    }

    [Fact]
    public void Opaque_converters_objects_deferred_choices_and_custom_IR_remain_conservative()
    {
        var fixture = new EmissionAnalysisFixture(); var type = fixture.Root.Type;
        var leaf = new BoundConstantExpression(null, type, default);
        foreach (var value in new BoundExpression[] { new BoundObjectExpression(fixture.Root),
            new BoundMarkupExpression(fixture.Root, fixture.Root.Constructor!, type, default),
            new BoundChoiceExpression(fixture.Root, [], leaf, type, default),
            new BoundDeferredExpression(leaf, type, default), new BoundConverterExpression("", type, type, default),
            new BoundValueConverterExpression(leaf, type, type, default),
            new BoundRawExpression("null", type, default), new BoundReferenceExpression("name", type, default), new Custom(type) })
        { Assert.True(Previous(value)); Assert.True(ValueEmitter.UsesFrame(value)); }
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(1024)]
    public void Admission_boundaries_keep_late_hazards_and_reference_distinct_records(int count)
    {
        var fixture = new EmissionAnalysisFixture(); var type = fixture.Root.Type;
        var children = Enumerable.Range(0, count).Select(_ => (BoundExpression)new BoundArrayExpression(
            [new BoundConstantExpression(null, type, default)], fixture.ArrayType, default)).ToImmutableArray();
        var root = new BoundArrayExpression(children, fixture.ArrayType, default);
        Assert.False(ValueEmitter.UsesFrame(root));
        var changed = root with { Values = children.Add(new BoundRawExpression("null", type, default)) };
        Assert.True(ValueEmitter.UsesFrame(changed));
        Assert.False(ValueEmitter.UsesFrame(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deep_shared_DAGs_do_not_expand_occurrences_or_hash_records_structurally(bool lateHazard)
    {
        var fixture = new EmissionAnalysisFixture();
        BoundExpression value = new BoundConstantExpression(null, fixture.Root.Type, default);
        for (var i = 0; i < 4096; i++) value = new BoundArrayExpression([value, value], fixture.ArrayType, default);
        if (lateHazard) value = new BoundArrayExpression([value, new BoundServiceExpression(fixture.Root.Type, default)], fixture.ArrayType, default);
        Assert.Equal(lateHazard, ValueEmitter.UsesFrame(value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unary_chains_are_stack_safe_without_admitting_a_large_visited_set(bool required)
    {
        var fixture = new EmissionAnalysisFixture(); var type = fixture.Root.Type;
        BoundExpression value = required ? new BoundServiceExpression(type, default) : new BoundConstantExpression(null, type, default);
        for (var i = 0; i < 100000; i++) value = new BoundCastExpression(value, type, default);
        Assert.Equal(required, ValueEmitter.UsesFrame(value));
    }

    [Fact]
    public void Seeded_DAGs_match_an_independent_occurrence_expanding_oracle()
    {
        var fixture = new EmissionAnalysisFixture(); var type = fixture.Root.Type; var random = new Random(731940);
        for (var sample = 0; sample < 500; sample++)
        {
            var nodes = new List<BoundExpression> { new BoundConstantExpression(null, type, default) };
            for (var i = 0; i < 24; i++)
            {
                var first = nodes[random.Next(nodes.Count)]; var second = nodes[random.Next(nodes.Count)];
                nodes.Add(random.Next(6) switch {
                    0 => new BoundCastExpression(first, type, default),
                    1 => new BoundCachedExpression("key", first, default),
                    2 => new BoundAssignmentExpression(first, second, default),
                    3 => new BoundServiceExpression(type, default),
                    _ => new BoundArrayExpression([first, second], fixture.ArrayType, default)
                });
            }
            foreach (var node in nodes) Assert.Equal(Previous(node), ValueEmitter.UsesFrame(node));
        }
    }

    [Fact]
    public void Hazard_short_circuit_does_not_read_later_initializer_or_call_records()
    {
        var fixture = new EmissionAnalysisFixture(); var hazard = new BoundServiceExpression(fixture.Root.Type, default);
        var creation = new BoundNewExpression(fixture.Root.Constructor!, [hazard], default)
        { Initializers = ImmutableArray.Create((BoundPropertyInitialization)null!) };
        var builder = new BoundBuilderExpression(hazard, ImmutableArray.Create((BoundBuilderCall)null!), fixture.Root.Constructor!, default);
        Assert.True(Previous(creation)); Assert.True(ValueEmitter.UsesFrame(creation));
        Assert.True(Previous(builder)); Assert.True(ValueEmitter.UsesFrame(builder));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Default_suffix_arrays_are_observed_only_after_an_independent_prefix(bool requiresFrame)
    {
        var compilation = CompilationFactory.Create(Model);
        var type = compilation.GetTypeByMetadataName("FrameCase.Item")!;
        var property = (IPropertySymbol)type.GetMembers("Value").Single();
        var method = (IMethodSymbol)type.GetMembers("Call").Single();
        var constructor = type.InstanceConstructors.Single(c => c.Parameters.IsEmpty);
        var constant = new BoundConstantExpression(null, type, default);
        BoundExpression prefix = requiresFrame ? new BoundServiceExpression(type, default) : constant;
        BoundExpression[] expressions = [
            new BoundCallExpression(method, prefix, default, default),
            new BoundPropertyAccessExpression(prefix, property, default, default),
            new BoundBuilderExpression(prefix, default, method, default),
            new BoundScopedInitializationExpression(prefix, constructor, default, property, default),
            new BoundNewExpression(constructor, [prefix], default) { Initializers = default }
        ];
        foreach (var expression in expressions)
        {
            var pair = new BoundArrayExpression([constant, constant], compilation.CreateArrayTypeSymbol(type), default);
            var wide = new BoundArrayExpression(Enumerable.Repeat<BoundExpression>(pair, 64)
                .Append(expression).ToImmutableArray(), compilation.CreateArrayTypeSymbol(type), default);
            foreach (var candidate in new[] { expression, wide })
            {
                if (requiresFrame)
                {
                    Assert.True(Previous(candidate));
                    Assert.True(ValueEmitter.UsesFrame(candidate));
                }
                else
                {
                    var expected = Record.Exception(() => Previous(candidate));
                    Assert.NotNull(expected);
                    var actual = Record.Exception(() => ValueEmitter.UsesFrame(candidate));
                    Assert.NotNull(actual);
                    Assert.Equal(expected.GetType(), actual.GetType());
                }
            }
        }
    }

    [Fact]
    public void Parallel_queries_have_no_shared_mutable_or_retained_analysis_state()
    {
        var fixture = new EmissionAnalysisFixture();
        BoundExpression independent = new BoundConstantExpression(null, fixture.Root.Type, default);
        for (var i = 0; i < 18; i++) independent = new BoundArrayExpression([independent, independent], fixture.ArrayType, default);
        var required = new BoundArrayExpression([independent, new BoundServiceExpression(fixture.Root.Type, default)], fixture.ArrayType, default);
        Parallel.For(0, 128, i => Assert.Equal(i % 2 != 0, ValueEmitter.UsesFrame(i % 2 == 0 ? independent : required)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Generated_execution_never_deduplicates_shared_constructions(bool sharedDocuments, bool service)
    {
        var profile = XamlFrameworkProfile.Portable with { MarkupBindingRules = [new ArrayRule(service)] };
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:FrameCase' Value='{Array}'/>", Model, profile, sharedDocuments);
        var first = code.Build(); var second = code.Build();
        var a = (object[])first.GetType().GetProperty("Value")!.GetValue(first)!;
        var b = (object[])second.GetType().GetProperty("Value")!.GetValue(second)!;
        var count = service ? 63 : 64;
        Assert.NotSame(a, b);
        for (var i = 0; i < count; i++)
        { Assert.NotSame(a[i], b[i]); for (var j = 0; j < i; j++) Assert.NotSame(a[i], a[j]); }
        Assert.Equal(count * 2, code.Assembly.GetType("FrameCase.Item")!.GetField("Created")!.GetValue(null));
        if (service) { Assert.IsAssignableFrom<IServiceProvider>(a[63]); Assert.IsAssignableFrom<IServiceProvider>(b[63]); }
        Assert.True(XamlRuntimeSession.TryGet(first, out var s1)); s1!.Dispose();
        Assert.True(XamlRuntimeSession.TryGet(second, out var s2)); s2!.Dispose();
    }

    [Fact]
    public void Empty_argument_lowering_does_not_allocate_per_constructor_or_touch_the_writer()
    {
        var fixture = new EmissionAnalysisFixture();
        using var context = new EmissionContext(fixture.Document, default);
        var values = new ValueEmitter(context, new ObjectEmitter(context));
        var first = values.EmitArguments(fixture.Root.Constructor!, [], "__frame");
        Assert.Same(Array.Empty<string>(), first);
        for (var i = 0; i < 100; i++)
            Assert.Same(first, values.EmitArguments(fixture.Root.Constructor!, [], "__frame"));
        Assert.Equal(string.Empty, context.Writer.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shared_empty_call_groups_count_toward_segment_work(bool scoped)
    {
        var fixture = new EmissionAnalysisFixture();
        var type = fixture.Root.Type;
        var literal = new BoundConstantExpression(null, type, default);
        var calls = Enumerable.Repeat(new BoundBuilderCall(fixture.Root.Constructor!, []), 1024).ToImmutableArray();
        BoundExpression node = scoped
            ? new BoundScopedInitializationExpression(literal, fixture.Root.Constructor!, calls, null, default)
            : new BoundBuilderExpression(literal, calls, fixture.Root.Constructor!, default);
        Assert.False(Previous(node));
        // A node-only budget would incorrectly finish despite scanning 1,024
        // empty argument groups; assert the bounded segment declines admission.
        var segment = typeof(CSharpEmitter).Assembly.GetType("XamlG.CSharp.FrameDependencyAnalysis")!
            .GetMethod("TrySegment", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        object?[] arguments = [node, 31, false];
        Assert.False((bool)segment.Invoke(null, arguments)!);
        var root = new BoundArrayExpression(Enumerable.Repeat(node, 1024).ToImmutableArray(), fixture.ArrayType, default);
        Assert.False(ValueEmitter.UsesFrame(root));
        Assert.True(ValueEmitter.UsesFrame(root with { Values = root.Values.Add(new BoundServiceExpression(type, default)) }));
    }

    private sealed record Custom(ITypeSymbol ValueType) : BoundExpression(ValueType, default);
    private sealed class ArrayRule(bool service) : IXamlMarkupBindingRule
    {
        public bool TryBind(BindingContext context, MarkupExtensionSyntax syntax, ITypeSymbol targetType, NamespaceScope scope, out BoundExpression? expression)
        {
            expression = null; if (syntax.Name != "Array") return false;
            var objectType = context.Types.Special(SpecialType.System_Object);
            var item = new BoundNewExpression(context.Types.Find("FrameCase.Item")!.InstanceConstructors.Single(c => c.Parameters.IsEmpty), [], syntax.Span);
            var values = Enumerable.Repeat<BoundExpression>(item, service ? 63 : 64).ToImmutableArray();
            if (service) values = values.Add(new BoundServiceExpression(context.Types.Find("System.IServiceProvider")!, syntax.Span));
            expression = new BoundArrayExpression(values, context.Types.Compilation.CreateArrayTypeSymbol(objectType), syntax.Span);
            return true;
        }
    }

    // Frozen ValueEmitter predicate; never forwards to candidate helpers.
    internal static bool Previous(BoundExpression value) => value switch
    {
        BoundConstantExpression or BoundEnumExpression or BoundTypeExpression or BoundMethodHandleExpression or BoundStaticExpression or BoundParseExpression => false,
        BoundCastExpression x => Previous(x.Value), BoundCachedExpression x => Previous(x.Value),
        BoundNewExpression x => x.Arguments.Any(Previous) || x.Initializers.Any(i => Previous(i.Value)),
        BoundBuilderExpression x => Previous(x.Creation) || x.Calls.Any(c => c.Arguments.Any(Previous)),
        BoundScopedInitializationExpression x => Previous(x.Creation) || x.Calls.Any(c => c.Arguments.Any(Previous)),
        BoundArrayExpression x => x.Values.Any(Previous), BoundCollectionExpression x => x.Values.Any(Previous),
        BoundCallExpression x => x.Receiver != null && Previous(x.Receiver) || x.Arguments.Any(Previous),
        BoundParameterExpression => false,
        BoundPropertyAccessExpression x => Previous(x.Receiver) || x.IndexArguments.Any(Previous),
        BoundFieldAccessExpression x => Previous(x.Receiver),
        BoundAssignmentExpression x => Previous(x.Target) || Previous(x.Value),
        BoundMethodGroupExpression x => x.Receiver != null && Previous(x.Receiver),
        BoundLambdaExpression x => Previous(x.Body), _ => true
    };
}
