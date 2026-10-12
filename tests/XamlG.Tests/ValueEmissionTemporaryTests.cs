using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ValueEmissionTemporaryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Ordinary_literal_emission_has_no_converter_or_initializer_capture(bool initialized, bool boolean)
    {
        var fixture = new EmissionAnalysisFixture();
        using var context = new EmissionContext(fixture.Document, default);
        var emitter = new ValueEmitter(context, new ObjectEmitter(context));
        var expression = new BoundConstantExpression(boolean ? (object)true : null, fixture.Root.Type, default);
        string? result = null;
        for (var i = 0; i < 512; i++)
            result = initialized ? emitter.EmitInitialized(expression, "__frame", [], []) : emitter.Emit(expression, "__frame");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            result = initialized ? emitter.EmitInitialized(expression, "__frame", [], []) : emitter.Emit(expression, "__frame");
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(result);
        Assert.Equal(0, bytes);
        Assert.Equal(boolean ? "true" : "null", result);
        Assert.Equal(string.Empty, context.Writer.ToString());
    }

    [Fact]
    public void Invalid_initializers_keep_the_occurrence_diagnostic_before_lowering()
    {
        var fixture = new EmissionAnalysisFixture();
        using var context = new EmissionContext(fixture.Document, default);
        var emitter = new ValueEmitter(context, new ObjectEmitter(context));
        var expression = new BoundConstantExpression(null, fixture.Root.Type, new(7, 4));
        var before = context.Diagnostics.Count;
        Assert.Equal("null", emitter.EmitInitialized(expression, "__frame",
            [new((IPropertySymbol)fixture.Member.Symbol, 0)], ["alreadyEvaluated"]));
        var diagnostic = Assert.Single(context.Diagnostics.Skip(before));
        Assert.Equal("XG1200", diagnostic.Code);
        Assert.Equal(expression.Span, diagnostic.Span);
        Assert.Equal("An argument initializer requires a construction expression.", diagnostic.Message);
    }

    [Fact]
    public void Cancellation_precedes_every_ordinary_value_write()
    {
        var fixture = new EmissionAnalysisFixture();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var context = new EmissionContext(fixture.Document, cancellation.Token);
        var emitter = new ValueEmitter(context, new ObjectEmitter(context));
        var expression = new BoundConstantExpression(null, fixture.Root.Type, default);
        Assert.Throws<OperationCanceledException>(() => emitter.Emit(expression, "__frame"));
        Assert.Throws<OperationCanceledException>(() => emitter.EmitInitialized(expression, "__frame", [], []));
        Assert.Equal(string.Empty, context.Writer.ToString());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Nested_converters_keep_construction_source_payload_failure_and_call_order(
        bool sharedDocuments, bool valuePayload, bool sourceInfo)
    {
        var profile = XamlFrameworkProfile.Portable with { BindingRules = [new ConversionRule(valuePayload)] };
        if (sourceInfo) profile = profile with { Runtime = profile.Runtime with { SourceInfo = new("ValueTemps.SourceInfo", "Set") } };
        using var code = CompiledXaml.Create("<Root xmlns='clr-namespace:ValueTemps' Value='payload'/>", Model, profile, sharedDocuments);
        var trace = code.Assembly.GetType("ValueTemps.Trace")!;
        var log = (List<string>)trace.GetField("Log")!.GetValue(null)!;
        var stages = new List<string> { "construct:outer" };
        if (sourceInfo) stages.Add("source:outer");
        stages.Add("construct:inner");
        if (sourceInfo) stages.Add("source:inner");
        if (valuePayload) stages.Add("payload");
        stages.Add("convert:inner"); stages.Add("convert:outer");
        object? previous = null;
        for (var build = 0; build < 2; build++)
        {
            log.Clear(); var root = code.Build();
            try
            {
                Assert.Equal(stages, log);
                var result = root.GetType().GetProperty("Value")!.GetValue(root)!;
                Assert.NotSame(previous, result); previous = result;
                var inner = result.GetType().GetField("Value")!.GetValue(result)!;
                Assert.NotSame(result, inner);
                Assert.Equal(valuePayload ? (object)17 : "payload", inner.GetType().GetField("Value")!.GetValue(inner));
            }
            finally { if (XamlRuntimeSession.TryGet(root, out var session)) session!.Dispose(); }
        }
        for (var i = 0; i < stages.Count; i++)
        {
            log.Clear(); trace.GetField("ThrowAt")!.SetValue(null, stages[i]);
            var error = Assert.Throws<TargetInvocationException>(() => code.Build());
            Assert.Equal(stages[i], Assert.IsType<InvalidOperationException>(error.InnerException).Message);
            Assert.Equal(stages.Take(i + 1), log);
        }
    }

    private const string Model = """
        using System;
        using System.Collections.Generic;
        using System.ComponentModel;
        using System.Globalization;
        namespace ValueTemps {
            public class Root { public object Value { get; set; } }
            public static class Trace {
                public static List<string> Log = new();
                public static string ThrowAt;
                public static void Hit(string value) { Log.Add(value); if (ThrowAt == value) throw new InvalidOperationException(value); }
                public static object Read() { Hit("payload"); return 17; }
                public static void Check(ITypeDescriptorContext context, CultureInfo culture) {
                    if (context == null || !ReferenceEquals(culture, CultureInfo.InvariantCulture)) throw new Exception("missing context/culture");
                }
            }
            public class Box { public object Value; public Box(object value) { Value = value; } }
            public class Outer : TypeConverter {
                public Outer() { Trace.Hit("construct:outer"); }
                public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value) {
                    Trace.Check(context, culture); Trace.Hit("convert:outer"); return new Box(value);
                }
            }
            public class Inner : TypeConverter {
                public Inner() { Trace.Hit("construct:inner"); }
                public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value) {
                    Trace.Check(context, culture); Trace.Hit("convert:inner"); return new Box(value);
                }
            }
            public class SourceInfo {
                public SourceInfo(int line, int column, string path) { }
                public static void Set(object value, SourceInfo source) {
                    if (value is Outer) Trace.Hit("source:outer");
                    if (value is Inner) Trace.Hit("source:inner");
                }
            }
        }
        """;

    private sealed class ConversionRule(bool valuePayload) : IXamlBindingRule
    {
        public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
        {
            if (attribute.Name != "Value") return false;
            var type = context.Types.Special(SpecialType.System_Object);
            var innerType = context.Types.Find("ValueTemps.Inner")!;
            BoundExpression inner = valuePayload
                ? new BoundValueConverterExpression(new BoundCallExpression(
                    (IMethodSymbol)context.Types.Find("ValueTemps.Trace")!.GetMembers("Read").Single(), null, [], attribute.ValueSpan), innerType, type, attribute.ValueSpan)
                : new BoundConverterExpression(attribute.Value, innerType, type, attribute.ValueSpan);
            var value = new BoundValueConverterExpression(inner, context.Types.Find("ValueTemps.Outer")!, type, attribute.ValueSpan);
            var property = (IPropertySymbol)target.Type.GetMembers("Value").Single();
            target.Assignments.Add(new BoundSetAssignment(new(property.Name, BoundMemberKind.Property, property, property.Type,
                property.GetMethod, property.SetMethod, attribute.NameSpan), value, attribute.Span));
            return true;
        }
    }
}
