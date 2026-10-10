using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class BuilderExpressionTests
{
    private const string Model = """
        namespace Builders;
        public class View {
            public static System.Collections.Generic.List<string> Log { get; } = new();
            public static bool Throw;
            public int Value { get; set; }
            public static string Read(string value) { Log.Add("read:" + value); return value; }
        }
        public class Token {
            public static implicit operator Token(string value) { View.Log.Add("convert:" + value); return new(); }
        }
        public TYPE Builder {
            private int _count;
            public Builder() { _count = 0; View.Log.Add("construct"); }
            public void First(Token value) { _count++; View.Log.Add("first"); if (View.Throw) throw new System.Exception("stop"); }
            public void Second(object value) { _count += 2; View.Log.Add("second"); }
            public int Build() { View.Log.Add("build"); return _count; }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuilderPreservesMutationsConversionOrderAndFailureShortCircuit(bool valueType)
    {
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Builders' Value='value'/>",
            Model.Replace("TYPE", valueType ? "struct" : "class", StringComparison.Ordinal),
            XamlFrameworkProfile.Portable with { BindingRules = ImmutableArray.Create<IXamlBindingRule>(new Rule()) });
        var root = code.Build();
        var view = code.Assembly.GetType("Builders.View")!;
        Assert.Equal(3, view.GetProperty("Value")!.GetValue(root));
        var log = Assert.IsType<List<string>>(view.GetProperty("Log")!.GetValue(null));
        Assert.Equal(new[] { "construct", "read:a", "convert:a", "first", "read:b", "second", "build" }, log);
        log.Clear();
        view.GetField("Throw")!.SetValue(null, true);
        Assert.Throws<System.Reflection.TargetInvocationException>(() => code.Build());
        Assert.Equal(new[] { "construct", "read:a", "convert:a", "first" }, log);
    }

    private sealed class Rule : IXamlBindingRule
    {
        public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
        {
            if (attribute.Name != "Value") return false;
            var builder = context.Types.Find("Builders.Builder")!;
            var read = (IMethodSymbol)target.Type.GetMembers("Read").Single();
            BoundExpression Read(string value) => new BoundCallExpression(read, null, ImmutableArray.Create<BoundExpression>(
                new BoundConstantExpression(value, context.Types.Special(SpecialType.System_String), attribute.Span)), attribute.Span);
            var second = Read("b");
            var value = new BoundBuilderExpression(new BoundNewExpression(builder.InstanceConstructors.Single(),
                ImmutableArray<BoundExpression>.Empty, attribute.Span), ImmutableArray.Create(
                    new BoundBuilderCall((IMethodSymbol)builder.GetMembers("First").Single(), ImmutableArray.Create(Read("a"))),
                    new BoundBuilderCall((IMethodSymbol)builder.GetMembers("Second").Single(), ImmutableArray.Create<BoundExpression>(
                        new BoundArrayExpression(ImmutableArray.Create(second), context.Types.Compilation.CreateArrayTypeSymbol(second.Type!), attribute.Span)))),
                (IMethodSymbol)builder.GetMembers("Build").Single(), attribute.Span);
            var property = (IPropertySymbol)target.Type.GetMembers("Value").Single();
            target.Assignments.Add(new BoundSetAssignment(new(property.Name, BoundMemberKind.Property, property,
                property.Type, property.GetMethod, property.SetMethod, attribute.Span), value, attribute.Span));
            return true;
        }
    }
}
