using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class CachedExpressionTests
{
    private const string Model = """
        namespace CachedValues;
        public class View { public object First { get; set; } public object Second { get; set; } }
        public sealed class Value
        {
            public static int Attempts;
            public static bool Fail;
            public Value() { Attempts++; if (Fail) throw new System.InvalidOperationException("constructor failed"); }
        }
        """;

    [Fact]
    public void EquivalentImmutableValuesAreLazyAndSharedAcrossBuilds()
    {
        using var code = Compile();
        var type = code.Assembly.GetType("CachedValues.Value")!;
        var attempts = type.GetField("Attempts")!;
        Assert.Equal(0, attempts.GetValue(null));
        var first = code.Build();
        var second = code.Build();
        var value = first.GetType().GetProperty("First")!.GetValue(first);
        Assert.NotSame(first, second);
        Assert.Same(value, first.GetType().GetProperty("Second")!.GetValue(first));
        Assert.Same(value, second.GetType().GetProperty("First")!.GetValue(second));
        Assert.Equal(1, attempts.GetValue(null));
    }

    [Fact]
    public void FailedConstructionDoesNotPoisonTheLazyValue()
    {
        using var code = Compile();
        var type = code.Assembly.GetType("CachedValues.Value")!;
        type.GetField("Fail")!.SetValue(null, true);
        var failure = Assert.Throws<TargetInvocationException>(() => code.Build());
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        type.GetField("Fail")!.SetValue(null, false);
        var root = code.Build();
        Assert.NotNull(root.GetType().GetProperty("First")!.GetValue(root));
        Assert.Equal(2, type.GetField("Attempts")!.GetValue(null));
    }

    private static CompiledXaml Compile() => CompiledXaml.Create(
        "<View xmlns='clr-namespace:CachedValues' First='{Cached}' Second='{Cached}'/>", Model,
        XamlFrameworkProfile.Portable with { MarkupBindingRules = ImmutableArray.Create<IXamlMarkupBindingRule>(new Rule()) });

    private sealed class Rule : IXamlMarkupBindingRule
    {
        public bool TryBind(BindingContext context, MarkupExtensionSyntax syntax, ITypeSymbol targetType,
            NamespaceScope scope, out BoundExpression? expression)
        {
            expression = syntax.Name == "Cached" ? new BoundCachedExpression("immutable value",
                new BoundNewExpression(context.Types.Find("CachedValues.Value")!.InstanceConstructors.Single(),
                    ImmutableArray<BoundExpression>.Empty, syntax.Span), syntax.Span) : null;
            return expression != null;
        }
    }
}
