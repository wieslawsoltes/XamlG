using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class TargetFrameExpressionTests
{
    private const string Model = """
        namespace FrameExpressions;
        public class View
        {
            public static int Descriptors;
            public object Value { get; set; }
            public static object Descriptor() { Descriptors++; return "Value"; }
        }
        public class Probe
        {
            public Probe(System.IServiceProvider services)
            {
                if (View.Descriptors != 1) throw new System.Exception("Descriptor must run first, exactly once");
                if (services == null) { Field = "literal"; return; }
                var target = (XamlG.Runtime.IXamlProvideValueTarget)services.GetService(typeof(XamlG.Runtime.IXamlProvideValueTarget));
                if (!Equals(target.TargetProperty, "Value")) throw new System.Exception("Wrong target property");
                Field = target.TargetObject;
            }
            public object Field;
            public object Property => Field;
            public object this[int index] => Field;
            public object Read() => Field;
            public static object Call(Probe probe) => probe.Field;
        }
        """;

    public static IEnumerable<object[]> Cases() =>
        from kind in new[] { "call", "receiver", "property", "field", "indexer", "assignment", "lambda", "methodgroup" }
        from services in new[] { false, true }
        select new object[] { kind, services };

    [Theory]
    [MemberData(nameof(Cases))]
    public void NestedExpressionsPreserveDescriptorOrderAndTargetServices(string kind, bool services)
    {
        var profile = XamlFrameworkProfile.Portable with
        {
            BindingRules = ImmutableArray.Create<IXamlBindingRule>(new ExpressionRule(kind, services))
        };
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:FrameExpressions' Value='test'/>", Model, profile);
        var root = code.Build();
        var value = root.GetType().GetProperty("Value")!.GetValue(root);
        if (value is Delegate callback) value = callback.DynamicInvoke();
        if (services) Assert.Same(root, value); else Assert.Equal("literal", value);
        Assert.Equal(services, code.Emission.Source.Contains(".ForTarget(", StringComparison.Ordinal));
    }

    private sealed class ExpressionRule(string kind, bool services) : IXamlBindingRule
    {
        public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
        {
            if (attribute.Name != "Value") return false;
            var span = attribute.Span;
            var property = (IPropertySymbol)target.Type.GetMembers("Value").Single();
            var descriptor = (IMethodSymbol)target.Type.GetMembers("Descriptor").Single();
            var member = new BoundMember(property.Name, BoundMemberKind.Property, property, property.Type,
                property.GetMethod, property.SetMethod, span)
            {
                TargetDescriptor = new BoundCallExpression(descriptor, null, ImmutableArray<BoundExpression>.Empty, span)
            };
            var serviceType = context.Types.Find("System.IServiceProvider")!;
            BoundExpression service = services ? new BoundServiceExpression(serviceType, span) : new BoundConstantExpression(null, serviceType, span);
            var type = context.Types.Find("FrameExpressions.Probe")!;
            var probe = new BoundNewExpression(type.InstanceConstructors.Single(), ImmutableArray.Create(service), span);
            var field = new BoundFieldAccessExpression(probe, (IFieldSymbol)type.GetMembers("Field").Single(), span);
            var read = (IMethodSymbol)type.GetMembers("Read").Single();
            var func = context.Types.Find("System.Func`1")!.Construct(context.Types.Special(SpecialType.System_Object));
            BoundExpression expression = kind switch
            {
                "call" => new BoundCallExpression((IMethodSymbol)type.GetMembers("Call").Single(), null, ImmutableArray.Create<BoundExpression>(probe), span),
                "receiver" => new BoundCallExpression(read, probe, ImmutableArray<BoundExpression>.Empty, span),
                "property" => new BoundPropertyAccessExpression(probe, (IPropertySymbol)type.GetMembers("Property").Single(), ImmutableArray<BoundExpression>.Empty, span),
                "field" => field,
                "indexer" => new BoundPropertyAccessExpression(probe, type.GetMembers().OfType<IPropertySymbol>().Single(p => p.IsIndexer),
                    ImmutableArray.Create<BoundExpression>(new BoundConstantExpression(0, context.Types.Special(SpecialType.System_Int32), span)), span),
                "assignment" => new BoundAssignmentExpression(field, field, span),
                "lambda" => new BoundLambdaExpression(func, ImmutableArray<BoundParameterExpression>.Empty, field, false, span),
                "methodgroup" => new BoundMethodGroupExpression(read, probe, func, span),
                _ => throw new InvalidOperationException(kind)
            };
            target.Assignments.Add(new BoundSetAssignment(member, expression, span));
            return true;
        }
    }
}
