using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class AttachedLookupIsolationTests
{
    private const string Model = """
        using System;
        namespace Fallback {
            public class Target { public string Own { get; set; } = ""; }
            public class Child : Target { }
            public class First { }
            public class Second { }
            public static class Attached {
                public static void SetValue(Target owner, First value) { }
                public static void SetValue(Child owner, Second value) { }
                public static void SetValue(Target owner, string value) { }
                public static First GetValue(Target owner) => new();
                public static First GetReadOnly(Target owner) => new();
                public static void SetWriteOnly(Target owner, First value) { }
                public static void AddChangedHandler(Target owner, Action handler) { }
                private static void SetHidden(Target owner, First value) { }
            }
        }
        """;
    private static readonly XamlSyntaxTree Syntax = XamlSyntaxTree.Parse("<Target xmlns='clr-namespace:Fallback'/>");
    private static readonly NamespaceScope Scope = NamespaceScope.Empty.Push(Syntax.Root!);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("blue")]
    public void Extracted_fallback_preserves_overload_ranking_and_stateful_conversion_order(string? hint)
    {
        var compilation = CompilationFactory.Create(Model);
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var types = new RoslynTypeSystem(compilation);
        var actualRule = new ObserveConversions();
        var previousRule = new ObserveConversions();
        var actualContext = Context(types, actualRule);
        var previousContext = Context(types, previousRule);
        var owner = types.Find("Fallback.Attached")!;
        // Alternate receiver types and run warm lookups; converter callbacks
        // deliberately return different answers on successive invocations.
        for (var round = 0; round < 3; round++)
        foreach (var target in new[] { types.Find("Fallback.Target")!, types.Find("Fallback.Child")! })
        foreach (var name in new[] { "Value", "ReadOnly", "WriteOnly", "Changed", "Hidden", "Missing" })
        {
            var span = new TextSpan(round + 1, 1);
            var expected = Previous(previousContext, target, owner, name, Scope, span, hint);
            var actual = actualContext.Members.Resolve(target, "Attached." + name, Scope, span, hint, report: false);
            Assert.Equal(expected, actual);
            Assert.Equal(previousRule.Trace.ToArray(), actualRule.Trace.ToArray());
            Assert.Equal(previousContext.Diagnostics.ToArray(), actualContext.Diagnostics.ToArray());
            Assert.Equal(previousContext.Symbols.ToArray(), actualContext.Symbols.Where(s => s.Role.StartsWith("attached-", StringComparison.Ordinal)).ToArray());
        }
        if (hint == null) Assert.Empty(actualRule.Trace);
        else Assert.NotEmpty(actualRule.Trace);
    }

    [Fact]
    public void Ordinary_hits_and_misses_never_probe_attached_value_conversions()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var observer = new ObserveConversions();
        var context = Context(types, observer);
        var target = types.Find("Fallback.Child")!;
        for (var i = 0; i < 100; i++)
        {
            Assert.NotNull(context.Members.Resolve(target, "Own", Scope, default, "text"));
            Assert.Null(context.Members.Resolve(target, "Missing", Scope, default, "text", report: false));
        }
        Assert.Empty(observer.Trace);
        Assert.Empty(context.Diagnostics);
    }

    private static BindingContext Context(RoslynTypeSystem types, IXamlTextConversionRule rule) =>
        BindingContext.CreateSignatureProbe(Syntax, types, XamlFrameworkProfile.Portable with
            { TextConversionRules = ImmutableArray.Create(rule) }, new());

    private sealed class ObserveConversions : IXamlTextConversionRule
    {
        public List<(string Text, string Target, TextSpan Span, bool Converted)> Trace { get; } = new();
        public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope,
            TextSpan span, ISymbol? member, out BoundExpression? expression)
        {
            var converted = Trace.Count % 2 == 0;
            Trace.Add((text, targetType.ToDisplayString(), span, converted));
            expression = converted ? new BoundConstantExpression(null, targetType, span) : null;
            return converted;
        }
    }

    // Frozen attached branch from ResolveCore. Keep its LINQ evaluation order:
    // speculative text conversions are observable to framework binding rules.
    private static BoundMember? Previous(BindingContext context, ITypeSymbol target, INamedTypeSymbol owner,
        string memberName, NamespaceScope scope, TextSpan span, string? valueHint)
    {
        var setters = owner.Members("Set" + memberName).OfType<IMethodSymbol>().Where(m => m.IsStatic && m.Parameters.Length == 2 &&
            context.Types.IsAccessible(m, context.RootClass) && context.Types.Compilation.ClassifyCommonConversion(target, m.Parameters[0].Type).IsImplicit).ToArray();
        var setter = setters.OrderBy(m => SymbolEqualityComparer.Default.Equals(target, m.Parameters[0].Type) ? 0 : 1)
            .ThenBy(m => valueHint == null || context.Values.TryText(valueHint, m.Parameters[1].Type, scope, span) != null ? 0 : 1).FirstOrDefault();
        var getter = owner.Members("Get" + memberName).OfType<IMethodSymbol>().FirstOrDefault(m => m.IsStatic && m.Parameters.Length == 1 &&
            context.Types.IsAccessible(m, context.RootClass) && context.Types.Compilation.ClassifyCommonConversion(target, m.Parameters[0].Type).IsImplicit);
        if (setter != null || getter != null)
        {
            var symbol = setter ?? getter!; context.Symbols.Add(new(span, symbol, "attached-property"));
            return new(memberName, BoundMemberKind.AttachedProperty, symbol, setter?.Parameters[1].Type ?? getter!.ReturnType, getter, setter, span);
        }
        var addHandler = owner.Members("Add" + memberName + "Handler").OfType<IMethodSymbol>().FirstOrDefault(m => m.IsStatic && m.Parameters.Length == 2 &&
            m.Parameters[1].Type.TypeKind == TypeKind.Delegate && context.Types.IsAccessible(m) && context.Types.Compilation.ClassifyCommonConversion(target, m.Parameters[0].Type).IsImplicit);
        if (addHandler != null)
        {
            context.Symbols.Add(new(span, addHandler, "attached-event"));
            return new(memberName, BoundMemberKind.AttachedEvent, addHandler, addHandler.Parameters[1].Type, null, addHandler, span);
        }
        return null;
    }
}
