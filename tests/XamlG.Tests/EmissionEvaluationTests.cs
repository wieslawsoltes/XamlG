using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class EmissionEvaluationTests
{
    private const string Model = """
        namespace Evaluation;
        public class View
        {
            public static System.Collections.Generic.List<string> Log { get; } = new();
            private object _value;
            public object Value { get => _value; set { Log.Add("set"); _value = value; } }
            public string Events => string.Join(",", Log);
            public static object Descriptor() { Log.Add("descriptor"); return "Value"; }
        }
        public class Argument
        {
            public static implicit operator Argument(string value) { View.Log.Add("convert:" + value); return new(); }
        }
        public class Item { public Item() { View.Log.Add("new"); } }
        public static class Methods
        {
            public static object GetValue(View target) => target.Value;
            public static void SetValue(View target, object value) => target.Value = value;
            public static object Call(Argument first, Item second, int third) { View.Log.Add("call:" + third); return second; }
            public static object Call(Argument first, Item second, byte third) => throw new System.Exception("Wrong overload");
        }
        """;

    [Fact]
    public void FrameworkDescriptorsRunBeforeValuesEvenWithoutTargetServices()
    {
        var profile = XamlFrameworkProfile.Portable with { BindingRules = ImmutableArray.Create<IXamlBindingRule>(new DescriptorRule()) };
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Evaluation' Value='new'/>", Model, profile);
        var root = code.Build();
        Assert.Equal("descriptor,new,set", root.GetType().GetProperty("Events")!.GetValue(root));
        Assert.Equal("Evaluation.Item", root.GetType().GetProperty("Value")!.GetValue(root)!.GetType().FullName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArgumentSpillsPreserveUserConversionsAndSelectedOverloads(bool narrowConstant)
    {
        var profile = XamlFrameworkProfile.Portable with { MarkupBindingRules = ImmutableArray.Create<IXamlMarkupBindingRule>(new CallRule(narrowConstant)) };
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Evaluation' Value='{Call}'/>", Model, profile);
        var root = code.Build();
        Assert.Equal("convert:first,new,call:7,set", root.GetType().GetProperty("Events")!.GetValue(root));
    }

    [Fact]
    public void AliasedMembersKeepDistinctLivePropertyNames()
    {
        var profile = XamlFrameworkProfile.Portable with { BindingRules = ImmutableArray.Create<IXamlBindingRule>(new AliasRule()) };
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Evaluation' First='first' Second='second'/>", Model, profile);
        var root = code.Build();
        Assert.Equal("second", root.GetType().GetProperty("Value")!.GetValue(root));
        Assert.True(XamlRuntimeSession.TryGet(root, out var session));
        var key = session!.FindNode(root)!.Key;
        Assert.True(session.Apply(0, new[] { new XamlPropertyUpdate(key, "First", "changed first") }).Applied);
        Assert.Equal("changed first", root.GetType().GetProperty("Value")!.GetValue(root));
        Assert.True(session.Apply(1, new[] { new XamlPropertyUpdate(key, "Second", "changed second") }).Applied);
        Assert.Equal("changed second", root.GetType().GetProperty("Value")!.GetValue(root));
    }

    private sealed class AliasRule : IXamlBindingRule
    {
        public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
        {
            if (attribute.Name is not ("First" or "Second")) return false;
            var methods = context.Types.Find("Evaluation.Methods")!;
            var getter = (IMethodSymbol)methods.GetMembers("GetValue").Single();
            var setter = (IMethodSymbol)methods.GetMembers("SetValue").Single();
            var member = new BoundMember(attribute.Name, BoundMemberKind.AttachedProperty, getter, getter.ReturnType, getter, setter, attribute.Span);
            target.Assignments.Add(new BoundSetAssignment(member,
                new BoundConstantExpression(attribute.Value, context.Types.Special(SpecialType.System_String), attribute.Span), attribute.Span));
            return true;
        }
    }

    private sealed class DescriptorRule : IXamlBindingRule
    {
        public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
        {
            if (attribute.Name != "Value") return false;
            var property = (IPropertySymbol)target.Type.GetMembers("Value").Single();
            var descriptor = (IMethodSymbol)target.Type.GetMembers("Descriptor").Single();
            var member = new BoundMember(property.Name, BoundMemberKind.Property, property, property.Type, property.GetMethod, property.SetMethod, attribute.Span)
            { TargetDescriptor = new BoundCallExpression(descriptor, null, ImmutableArray<BoundExpression>.Empty, attribute.Span) };
            target.Assignments.Add(new BoundSetAssignment(member, Item(context, attribute.Span), attribute.Span));
            return true;
        }
    }

    private sealed class CallRule(bool narrowConstant) : IXamlMarkupBindingRule
    {
        public bool TryBind(BindingContext context, MarkupExtensionSyntax syntax, ITypeSymbol targetType, NamespaceScope scope, out BoundExpression? expression)
        {
            expression = null;
            if (syntax.Name != "Call") return false;
            var method = context.Types.Find("Evaluation.Methods")!.GetMembers("Call").OfType<IMethodSymbol>()
                .Single(candidate => candidate.Parameters[2].Type.SpecialType == SpecialType.System_Int32);
            expression = new BoundCallExpression(method, null, ImmutableArray.Create<BoundExpression>(
                new BoundConstantExpression("first", context.Types.Special(SpecialType.System_String), syntax.Span),
                Item(context, syntax.Span), new BoundConstantExpression(narrowConstant ? (object)(byte)7 : 7,
                    context.Types.Special(narrowConstant ? SpecialType.System_Byte : SpecialType.System_Int32), syntax.Span)), syntax.Span);
            return true;
        }
    }

    private static BoundNewExpression Item(BindingContext context, TextSpan span) =>
        new(context.Types.Find("Evaluation.Item")!.InstanceConstructors.Single(), ImmutableArray<BoundExpression>.Empty, span);
}
