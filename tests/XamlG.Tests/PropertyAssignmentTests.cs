using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class PropertyAssignmentTests
{
    private const string Model = """
        using System;
        using System.Collections.Generic;
        using XamlG.Runtime;
        namespace PropertyHelpers;
        public enum Choice { Zero, One }
        public class Base { public virtual object Value { get; set; } }
        public class View : Base
        {
            public static readonly List<string> Log = new();
            public static string FailAt;
            public static int Descriptors;
            public View()
            {
                var owner = new XamlRuntimeSession();
                owner.TrackCleanup(() => Event("dispose"));
                owner.Attach(this);
            }
            public static void Event(string value)
            {
                Log.Add(value);
                if (value == FailAt) throw new InvalidOperationException(value);
            }
            public static object Descriptor { get { Event("descriptor:" + ++Descriptors); return "Value"; } }
            public override object Value { get => base.Value; set { Event("set:" + value); base.Value = value; } }
            private Payload _converted;
            public Payload Converted { get => _converted; set { Event("set:" + value.Number); _converted = value; } }
            public byte Narrow { get; set; }
            public Choice Mode { get; set; }
            public int? Optional { get; set; }
        }
        public class Payload
        {
            public int Number;
            public Payload(int value) { View.Event("new:" + value); Number = value; }
            public static implicit operator Payload(int value) { View.Event("convert:" + value); return new Payload(value); }
            public override string ToString() => Number.ToString();
        }
        public struct Box
        {
            public int Number;
            public Box(int value) { View.Event("box:" + value); Number = value; }
            public override string ToString() => Number.ToString();
        }
        """;

    [Theory]
    [InlineData("new", "Converted", "descriptor:1,new:1,set:1,descriptor:2,new:2,set:2")]
    [InlineData("implicit", "Converted", "descriptor:1,convert:1,new:1,set:1,descriptor:2,convert:2,new:2,set:2")]
    [InlineData("box", "Value", "descriptor:1,box:1,set:1,descriptor:2,box:2,set:2")]
    public void SharedAssignmentsKeepEvaluationOrderVirtualDispatchAndEditing(string kind, string property, string expected)
    {
        using var code = Compile(kind, property);
        var root = code.Build();
        Assert.Equal(expected, Events(code));
        var value = root.GetType().GetProperty(property)!.GetValue(root)!;
        Assert.Equal(2, value.GetType().GetField("Number")!.GetValue(value));
        if (kind == "box") Assert.True(value.GetType().IsValueType);
        Assert.Contains(".Assign", code.Emission.Source, StringComparison.Ordinal);
        Assert.True(XamlRuntimeSession.TryGet(root, out var session));
        var replacement = Activator.CreateInstance(value.GetType(), new object[] { 7 });
        Assert.True(session!.Apply(0, [new XamlPropertyUpdate(session.FindNode(root)!.Key, property, replacement)]).Applied);
        var edited = root.GetType().GetProperty(property)!.GetValue(root)!;
        Assert.Equal(7, edited.GetType().GetField("Number")!.GetValue(edited));
        Assert.EndsWith("set:7", Events(code), StringComparison.Ordinal);
        session.Dispose(); session.Dispose();
        Assert.EndsWith("set:7,dispose", Events(code), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("descriptor:2", "descriptor:1,convert:1,new:1,set:1,descriptor:2,dispose")]
    [InlineData("convert:2", "descriptor:1,convert:1,new:1,set:1,descriptor:2,convert:2,dispose")]
    [InlineData("new:2", "descriptor:1,convert:1,new:1,set:1,descriptor:2,convert:2,new:2,dispose")]
    [InlineData("set:2", "descriptor:1,convert:1,new:1,set:1,descriptor:2,convert:2,new:2,set:2,dispose")]
    public void FailuresRetainTheirPhaseExceptionAndCleanup(string phase, string expected)
    {
        using var code = Compile("implicit", "Converted");
        code.Assembly.GetType("PropertyHelpers.View")!.GetField("FailAt")!.SetValue(null, phase);
        var error = Assert.Throws<TargetInvocationException>(() => code.Build());
        Assert.Equal(phase, Assert.IsType<InvalidOperationException>(error.InnerException).Message);
        Assert.Equal(expected, Events(code));
        Assert.Contains(".Assign", code.Emission.Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("narrow", "Narrow")]
    [InlineData("zero", "Mode")]
    [InlineData("enum", "Mode")]
    [InlineData("null", "Optional")]
    public void ArgumentConversionPreservesConstantNarrowingEnumsAndNull(string kind, string property)
    {
        using var code = Compile(kind, property);
        var root = code.Build();
        var value = root.GetType().GetProperty(property)!.GetValue(root);
        if (kind == "narrow") Assert.Equal((byte)255, value);
        else if (kind == "null") Assert.Null(value);
        else Assert.Equal(kind == "zero" ? 0 : 1, Convert.ToInt32(value, CultureInfo.InvariantCulture));
        Assert.Equal("descriptor:1,descriptor:2", Events(code));
        Assert.Contains(".Assign", code.Emission.Source, StringComparison.Ordinal);
        Assert.True(XamlRuntimeSession.TryGet(root, out var session)); session!.Dispose();
    }

    private static CompiledXaml Compile(string kind, string property) => CompiledXaml.Create(
        "<View xmlns='clr-namespace:PropertyHelpers' S1='1' S2='2'/>", Model,
        XamlFrameworkProfile.Portable with { BindingRules = ImmutableArray.Create<IXamlBindingRule>(new Rule(kind, property)) },
        shareAcrossDocuments: true);

    [Fact]
    public void DynamicValuesRetainAssignmentBindingWithoutDynamicHelperDispatch()
    {
        using var code = Compile("dynamic", "Converted");
        var root = code.Build();
        Assert.Equal("descriptor:1,convert:1,new:1,set:1,descriptor:2,convert:2,new:2,set:2", Events(code));
        Assert.DoesNotContain(".Assign", code.Emission.Source, StringComparison.Ordinal);
        var value = root.GetType().GetProperty("Converted")!.GetValue(root)!;
        Assert.Equal(2, value.GetType().GetField("Number")!.GetValue(value));
        Assert.True(XamlRuntimeSession.TryGet(root, out var session)); session!.Dispose();
    }

    private static string Events(CompiledXaml code) => string.Join(",",
        ((IEnumerable)code.Assembly.GetType("PropertyHelpers.View")!.GetField("Log")!.GetValue(null)!).Cast<string>());

    private sealed class Rule(string kind, string propertyName) : IXamlBindingRule
    {
        public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
        {
            if (!attribute.Name.StartsWith("S", StringComparison.Ordinal)) return false;
            var property = (IPropertySymbol)(propertyName == "Value" ? target.Type.BaseType! : target.Type).GetMembers(propertyName).Single();
            var descriptor = (IPropertySymbol)target.Type.GetMembers("Descriptor").Single();
            var member = new BoundMember(property.Name, BoundMemberKind.Property, property, property.Type,
                property.GetMethod, property.SetMethod, attribute.Span)
                { TargetDescriptor = new BoundStaticExpression(descriptor, descriptor.Type, attribute.Span) };
            BoundExpression number = new BoundConstantExpression(int.Parse(attribute.Value, CultureInfo.InvariantCulture),
                context.Types.Special(SpecialType.System_Int32), attribute.Span);
            BoundExpression value = kind switch
            {
                "new" or "box" => new BoundNewExpression(context.Types.Find("PropertyHelpers." + (kind == "new" ? "Payload" : "Box"))!
                    .InstanceConstructors.Single(ctor => ctor.Parameters.Length == 1), ImmutableArray.Create(number), attribute.Span),
                "narrow" or "zero" => new BoundConstantExpression(kind == "zero" ? 0 : 255, context.Types.Special(SpecialType.System_Int32), attribute.Span),
                "enum" => new BoundEnumExpression(ImmutableArray.Create((IFieldSymbol)property.Type.GetMembers("One").Single()), property.Type, attribute.Span),
                "null" => new BoundConstantExpression(null, property.Type, attribute.Span),
                "dynamic" => new BoundCastExpression(number, context.Types.Compilation.DynamicType, attribute.Span),
                _ => number
            };
            target.Assignments.Add(new BoundSetAssignment(member, value, attribute.Span));
            return true;
        }
    }
}
