using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class NewInitializerTests
{
    private const string Model = """
        namespace Initializers;
        public class View {
            public static System.Collections.Generic.List<string> Log { get; } = new();
            public static bool Throw;
            public object Value { get; set; }
            public static string Read(string value) { Log.Add("read:" + value); return value; }
        }
        public class Token {
            public static implicit operator Token(string value) { View.Log.Add("convert:" + value); return new(); }
        }
        public TYPE Item {
            public Item() { View.Log.Add("construct"); }
            public Token First { init { View.Log.Add("first"); if (View.Throw) throw new System.Exception("stop"); } }
            public object Second { init { View.Log.Add("second"); } }
        }
        """;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InitializationPreservesConstructionConversionAndSetterOrder(bool statementValue, bool valueType)
    {
        using var code = Compile(statementValue, valueType);
        code.Build();
        var view = code.Assembly.GetType("Initializers.View")!;
        var log = Assert.IsType<List<string>>(view.GetProperty("Log")!.GetValue(null));
        Assert.Equal(new[] { "construct", "read:a", "convert:a", "first", "read:b", "second" }, log);
        Assert.Equal(statementValue, code.Emission.Source.Contains("UnsafeAccessor", StringComparison.Ordinal));
        log.Clear();
        view.GetField("Throw")!.SetValue(null, true);
        Assert.Throws<System.Reflection.TargetInvocationException>(() => code.Build());
        Assert.Equal(new[] { "construct", "read:a", "convert:a", "first" }, log);
    }

    private static CompiledXaml Compile(bool statementValue, bool valueType) => CompiledXaml.Create(
        "<View xmlns='clr-namespace:Initializers' Value='value'/>", Model.Replace("TYPE", valueType ? "struct" : "class", StringComparison.Ordinal),
        XamlFrameworkProfile.Portable with { BindingRules = ImmutableArray.Create<IXamlBindingRule>(new Rule(statementValue)) });

    private sealed class Rule(bool statementValue) : IXamlBindingRule
    {
        public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
        {
            if (attribute.Name != "Value") return false;
            var item = context.Types.Find("Initializers.Item")!;
            var read = (IMethodSymbol)target.Type.GetMembers("Read").Single();
            BoundExpression Read(string value) => new BoundCallExpression(read, null, ImmutableArray.Create<BoundExpression>(
                new BoundConstantExpression(value, context.Types.Special(SpecialType.System_String), attribute.Span)), attribute.Span);
            var second = Read("b");
            if (statementValue) second = new BoundArrayExpression(ImmutableArray.Create(second),
                context.Types.Compilation.CreateArrayTypeSymbol(second.Type!), attribute.Span);
            var value = new BoundNewExpression(item.InstanceConstructors.Single(), ImmutableArray<BoundExpression>.Empty, attribute.Span)
            {
                Initializers = ImmutableArray.Create(
                    new BoundPropertyInitialization((IPropertySymbol)item.GetMembers("First").Single(), Read("a")),
                    new BoundPropertyInitialization((IPropertySymbol)item.GetMembers("Second").Single(), second))
            };
            var property = (IPropertySymbol)target.Type.GetMembers("Value").Single();
            target.Assignments.Add(new BoundSetAssignment(new(property.Name, BoundMemberKind.Property, property,
                property.Type, property.GetMethod, property.SetMethod, attribute.Span), value, attribute.Span));
            return true;
        }
    }
}
