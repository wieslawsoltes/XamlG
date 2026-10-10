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

public sealed class LiteralAssignmentTests
{
    private const string Model = """
        using System;
        using System.Collections.Generic;
        using XamlG.Runtime;
        namespace ScalarHelpers;
        public class Base
        {
            public virtual object Value { get; set; }
        }
        public class View : Base
        {
            public static List<string> Log = new();
            public static List<object> Values = new();
            public static string FailAt;
            public static int Descriptors;
            public static int Released;
            public View()
            {
                var session = new XamlRuntimeSession();
                session.TrackCleanup(() => { Released++; Log.Add("dispose"); });
                session.Attach(this);
            }
            public static object Descriptor
            {
                get { Event("descriptor:" + ++Descriptors); return "Value"; }
            }
            public override object Value
            {
                get => base.Value;
                set { Event("set:" + value); Values.Add(value); base.Value = value; }
            }
            public byte Narrow { get; set; }
            public Payload Converted { get; set; }
            public int? Optional { get; set; }
            public static void Event(string value)
            {
                Log.Add(value);
                if (value == FailAt) throw new InvalidOperationException(value);
            }
        }
        public class Payload
        {
            public int Number;
            public static implicit operator Payload(int value)
            { View.Event("convert:" + value); return new Payload { Number = value }; }
        }
        """;

    [Fact]
    public void SharedSettersPreserveDescriptorsVirtualDispatchBoxingSourceLocationsAndEditing()
    {
        const string xaml = "<View xmlns='clr-namespace:ScalarHelpers' S1='i:1' S2='i:2' S3='s:three' S4='s:four'/>";
        using var code = Compile(xaml);
        var root = code.Build();
        Assert.Equal("descriptor:1,set:1,descriptor:2,set:2,descriptor:3,set:three,descriptor:4,set:four", Events(code));
        var values = ((IEnumerable)code.Assembly.GetType("ScalarHelpers.View")!.GetField("Values")!.GetValue(null)!).Cast<object>().ToArray();
        Assert.IsType<int>(values[0]); Assert.IsType<int>(values[1]); Assert.IsType<string>(values[2]);
        Assert.Equal(2, code.Emission.Source.Split("private static void __XamlGSetScalar_", StringSplitOptions.None).Length - 1);
        Assert.Contains(code.Emission.SourceMappings, mapping =>
            code.Emission.Source.Substring(mapping.GeneratedSpan.Start, mapping.GeneratedSpan.Length).Contains("three", StringComparison.Ordinal) &&
            xaml.Substring(mapping.SourceSpan.Start, mapping.SourceSpan.Length).Contains("three", StringComparison.Ordinal));
        Assert.True(XamlRuntimeSession.TryGet(root, out var session));
        Assert.True(session!.Apply(0, [new XamlPropertyUpdate(session.FindNode(root)!.Key, "Value", "edited")]).Applied);
        Assert.Equal("edited", root.GetType().GetProperty("Value")!.GetValue(root));
        Assert.EndsWith(",set:edited", Events(code), StringComparison.Ordinal);
        session.Dispose(); session.Dispose();
        Assert.Equal(1, code.Assembly.GetType("ScalarHelpers.View")!.GetField("Released")!.GetValue(null));
    }

    [Theory]
    [InlineData("descriptor:2", "descriptor:1,set:1,descriptor:2,dispose")]
    [InlineData("set:2", "descriptor:1,set:1,descriptor:2,set:2,dispose")]
    public void SharedSetterFailuresPreserveTheOriginalExceptionAndConstructionCleanup(string failure, string events)
    {
        using var code = Compile("<View xmlns='clr-namespace:ScalarHelpers' S1='i:1' S2='i:2'/>");
        code.Assembly.GetType("ScalarHelpers.View")!.GetField("FailAt")!.SetValue(null, failure);
        var error = Assert.Throws<TargetInvocationException>(() => code.Build());
        Assert.Equal(failure, Assert.IsType<InvalidOperationException>(error.InnerException).Message);
        Assert.Equal(events, Events(code));
        Assert.Equal(1, code.Assembly.GetType("ScalarHelpers.View")!.GetField("Released")!.GetValue(null));
        Assert.Contains("private static void __XamlGSetScalar_", code.Emission.Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Narrow")]
    [InlineData("Converted")]
    public void ConstantDependentAndUserDefinedConversionsKeepTheirInlineSemantics(string property)
    {
        using var code = Compile("<View xmlns='clr-namespace:ScalarHelpers' S1='i:1' S2='i:255'/>", property);
        var root = code.Build();
        var value = root.GetType().GetProperty(property)!.GetValue(root)!;
        if (property == "Narrow") Assert.Equal((byte)255, Assert.IsType<byte>(value));
        else Assert.Equal(255, value.GetType().GetField("Number")!.GetValue(value));
        Assert.Equal(property == "Narrow" ? "descriptor:1,descriptor:2" :
            "descriptor:1,convert:1,descriptor:2,convert:255", Events(code));
        Assert.DoesNotContain("__XamlGSetScalar_", code.Emission.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedScalarAssignmentRetainsNullableWrapping()
    {
        using var code = Compile("<View xmlns='clr-namespace:ScalarHelpers' S1='i:1' S2='i:2'/>", "Optional");
        var root = code.Build();
        Assert.Equal(2, root.GetType().GetProperty("Optional")!.GetValue(root));
        Assert.Equal("descriptor:1,descriptor:2", Events(code));
        Assert.Contains("private static void __XamlGSetScalar_", code.Emission.Source, StringComparison.Ordinal);
    }

    private static CompiledXaml Compile(string xaml, string property = "Value") => CompiledXaml.Create(xaml, Model,
        XamlFrameworkProfile.Portable with { BindingRules = ImmutableArray.Create<IXamlBindingRule>(new ScalarRule(property)) });

    private static string Events(CompiledXaml code) => string.Join(",",
        ((IEnumerable)code.Assembly.GetType("ScalarHelpers.View")!.GetField("Log")!.GetValue(null)!).Cast<string>());

    private sealed class ScalarRule(string propertyName) : IXamlBindingRule
    {
        public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
        {
            if (!attribute.Name.StartsWith("S", StringComparison.Ordinal)) return false;
            var property = (IPropertySymbol)(propertyName == "Value" ? target.Type.BaseType! : target.Type).GetMembers(propertyName).Single();
            var descriptor = (IPropertySymbol)target.Type.GetMembers("Descriptor").Single();
            var member = new BoundMember(property.Name, BoundMemberKind.Property, property, property.Type,
                property.GetMethod, property.SetMethod, attribute.Span)
            { TargetDescriptor = new BoundStaticExpression(descriptor, descriptor.Type, attribute.Span) };
            var text = attribute.Value.Substring(2);
            var integer = attribute.Value.StartsWith("i:", StringComparison.Ordinal);
            object value = integer ? int.Parse(text, CultureInfo.InvariantCulture) : text;
            target.Assignments.Add(new BoundSetAssignment(member, new BoundConstantExpression(value,
                context.Types.Special(integer ? SpecialType.System_Int32 : SpecialType.System_String), attribute.Span), attribute.Span));
            return true;
        }
    }
}
