using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Roslyn;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class CastChainEmissionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(32)]
    [InlineData(256)]
    [InlineData(1024)]
    public void Formatting_matches_frozen_recursive_spelling(int depth)
    {
        var fixture = new EmissionAnalysisFixture();
        using var context = new EmissionContext(fixture.Document, default);
        var emitter = new ValueEmitter(context, new ObjectEmitter(context));
        var compilation = CompilationFactory.Create("public class Example<T> { }");
        var types = new[] { compilation.GetSpecialType(SpecialType.System_Object),
            compilation.GetSpecialType(SpecialType.System_String),
            compilation.GetTypeByMetadataName("Example`1")!.Construct(compilation.GetSpecialType(SpecialType.System_Int32)) };
        BoundExpression expression = new BoundConstantExpression(null, types[0], new(7, 1));
        var expected = "null";
        for (var i = 0; i < depth; i++)
        {
            var type = types[i % types.Length];
            expression = new BoundCastExpression(expression, type, new(i, 1));
            expected = "((" + type.CSharpName() + ")(" + expected + "))";
        }
        Assert.Equal(expected, emitter.Emit(expression, "__frame"));
        Assert.Equal(string.Empty, context.Writer.ToString());
        Assert.Empty(context.Mappings);
    }

    [Fact]
    public void Very_deep_cast_only_formatting_does_not_consume_recursive_stack()
    {
        const int depth = 32768;
        var fixture = new EmissionAnalysisFixture();
        using var context = new EmissionContext(fixture.Document, default);
        var emitter = new ValueEmitter(context, new ObjectEmitter(context));
        BoundExpression expression = new BoundConstantExpression(null, fixture.Root.Type, default);
        var prefix = "((" + fixture.Root.Type.CSharpName() + ")(";
        var expected = new StringBuilder();
        for (var i = 0; i < depth; i++)
        {
            expression = new BoundCastExpression(expression, fixture.Root.Type, default);
            expected.Append(prefix);
        }
        expected.Append("null").Append(')', depth * 2);
        Assert.Equal(expected.ToString(), emitter.Emit(expression, "__frame"));
    }

    [Fact]
    public void Canceled_chain_does_not_lower_terminal_or_publish_source()
    {
        var fixture = new EmissionAnalysisFixture();
        using var cancel = new CancellationTokenSource();
        using var context = new EmissionContext(fixture.Document, cancel.Token);
        BoundExpression expression = new BoundConstantExpression(null, fixture.Root.Type, default);
        for (var i = 0; i < 1024; i++) expression = new BoundCastExpression(expression, fixture.Root.Type, default);
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => new ValueEmitter(context, new ObjectEmitter(context)).Emit(expression, "frame"));
        Assert.Equal(string.Empty, context.Writer.ToString());
        Assert.Empty(context.Mappings);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 16)]
    [InlineData(true, 16)]
    public void User_conversions_keep_every_runtime_occurrence_and_failure(bool shared, int depth)
    {
        var profile = XamlFrameworkProfile.Portable with { BindingRules = [new CastRule(depth)] };
        using var code = CompiledXaml.Create("<Root xmlns='clr-namespace:CastExecution' Value='17'/>", Model, profile, shared);
        var trace = code.Assembly.GetType("CastExecution.Trace")!;
        var log = (List<string>)trace.GetField("Log")!.GetValue(null)!;
        object? previous = null;
        for (var build = 0; build < 2; build++)
        {
            log.Clear(); trace.GetField("Step")!.SetValue(null, 0);
            var root = code.Build();
            try
            {
                Assert.Equal(new[] { "read" }.Concat(Enumerable.Range(1, depth).Select(i => i % 2 == 1 ? "B" : "A")), log);
                var value = root.GetType().GetProperty("Value")!.GetValue(root);
                Assert.NotSame(previous, value); previous = value;
            }
            finally { if (XamlRuntimeSession.TryGet(root, out var session)) session!.Dispose(); }
        }
        for (var step = 1; step <= depth + 1; step++)
        {
            log.Clear(); trace.GetField("Step")!.SetValue(null, 0); trace.GetField("FailAt")!.SetValue(null, step);
            var error = Assert.Throws<TargetInvocationException>(() => code.Build());
            Assert.IsType<InvalidOperationException>(error.InnerException);
            Assert.Equal(step, log.Count);
        }
    }

    private const string Model = """
        using System;
        using System.Collections.Generic;
        namespace CastExecution {
            public class Root { public object Value { get; set; } }
            public static class Trace {
                public static List<string> Log = new(); public static int Step, FailAt;
                public static void Hit(string name) { Log.Add(name); if (++Step == FailAt) throw new InvalidOperationException(name); }
                public static A Read() { Hit("read"); return new A(); }
            }
            public class A { public static explicit operator B(A value) { Trace.Hit("B"); return new B(); } }
            public class B { public static explicit operator A(B value) { Trace.Hit("A"); return new A(); } }
        }
        """;

    private sealed class CastRule(int depth) : IXamlBindingRule
    {
        public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
        {
            if (attribute.Name != "Value") return false;
            BoundExpression expression = new BoundCallExpression(
                (IMethodSymbol)context.Types.Find("CastExecution.Trace")!.GetMembers("Read").Single(), null, [], attribute.ValueSpan);
            for (var i = 0; i < depth; i++)
                expression = new BoundCastExpression(expression, context.Types.Find(i % 2 == 0 ? "CastExecution.B" : "CastExecution.A")!, attribute.ValueSpan);
            var property = (IPropertySymbol)target.Type.GetMembers("Value").Single();
            target.Assignments.Add(new BoundSetAssignment(new(property.Name, BoundMemberKind.Property, property, property.Type,
                property.GetMethod, property.SetMethod, attribute.NameSpan), expression, attribute.Span));
            return true;
        }
    }
}
