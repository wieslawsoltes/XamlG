using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class TextConversionMetadataTests
{
    private static readonly Func<ValueBinder, ITypeSymbol, IMethodSymbol?> Parse = typeof(ValueBinder)
        .GetMethod("FindParse", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<Func<ValueBinder, ITypeSymbol, IMethodSymbol?>>();
    private static readonly Func<ValueBinder, ISymbol?, INamedTypeSymbol?> Converter = typeof(ValueBinder)
        .GetMethod("FindConverter", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<Func<ValueBinder, ISymbol?, INamedTypeSymbol?>>();

    private const string Model = """
        #nullable enable
        using System;
        using System.ComponentModel;
        using System.Globalization;
        namespace ConversionModel {
            public class Pair {
                public static Pair Parse(string text) => new();
                public static Pair Parse(string text, IFormatProvider provider) => new();
                public static Pair Parse(string text, CultureInfo culture) => new();
            }
            public class Reversed {
                public static Reversed Parse(string text, CultureInfo culture) => new();
                public static Reversed Parse(string text, IFormatProvider provider) => new();
            }
            public class Base {
                public static Derived Parse(string text, IFormatProvider provider) => new();
            }
            public class Derived : Base { public static Derived Parse(string text) => new(); }
            public class Generic<T> { public static Generic<T> Parse(string text) => new(); }
            public class Invalid {
                public Invalid Parse(string text) => this;
                public static Invalid Parse<T>(string text, IFormatProvider provider) => new();
                private static Invalid Parse(string text, CultureInfo provider) => new();
                public static int Parse(string text, int ignored) => 0;
            }
            public class Empty { }
            [TypeConverter(typeof(Good))] public class Token { }
            [TypeConverter(" ConversionModel.Other , ignored, Version=1.0.0.0 ")] public class NamedToken { }
            [TypeConverter(typeof(NoConstructor))] public class InvalidToken { }
            [Obsolete] public class Unrelated { }
            public class Good : TypeConverter { }
            public class Other : TypeConverter { }
            public class NoConstructor : TypeConverter { public NoConstructor(int ignored) { } }
            public class NotAConverter { }
            public class Holder {
                [TypeConverter(typeof(Good))] public Pair Overridden { get; set; } = new();
                public Generic<string?> Annotated { get; set; } = new();
                public Generic<string> Plain { get; set; } = new();
                public Generic<(int Left, int Right)> Left { get; set; } = new();
                public Generic<(int X, int Y)> Right { get; set; } = new();
            }
        }
        """;

    private static BindingContext Context(RoslynTypeSystem types, XamlFrameworkProfile? profile = null,
        CancellationToken cancellation = default) => BindingContext.CreateSignatureProbe(
            XamlSyntaxTree.Parse("<Empty xmlns='clr-namespace:ConversionModel'/>", "Current.xaml"), types,
            profile ?? XamlFrameworkProfile.Portable, new(), cancellation);

    [Theory]
    [InlineData("Pair")]
    [InlineData("Reversed")]
    [InlineData("Derived")]
    [InlineData("Invalid")]
    [InlineData("Empty")]
    public void Cold_and_warm_parse_selection_preserve_the_frozen_legacy_ranking(string name)
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var target = types.Find("ConversionModel." + name)!;
        var expected = PreviousParse(types, target);
        for (var i = 0; i < 4; i++) Assert.Same(expected, Parse(Context(types).Values, target));
    }

    [Fact]
    public void Constructed_nullable_and_tuple_named_targets_keep_exact_symbol_identity()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var properties = types.Find("ConversionModel.Holder")!.GetMembers().OfType<IPropertySymbol>().Skip(1).ToArray();
        foreach (var property in properties.Concat(properties.Reverse()))
        {
            var selected = Parse(Context(types).Values, property.Type);
            Assert.Same(PreviousParse(types, property.Type), selected);
            Assert.True(SymbolEqualityComparer.IncludeNullability.Equals(selected!.ReturnType, property.Type));
        }
    }

    [Theory]
    [InlineData("Token")]
    [InlineData("NamedToken")]
    [InlineData("InvalidToken")]
    [InlineData("Unrelated")]
    [InlineData("Empty")]
    public void Attribute_converter_selection_and_misses_match_previous_metadata_rules(string name)
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var target = types.Find("ConversionModel." + name)!;
        for (var i = 0; i < 4; i++) Assert.Same(PreviousConverter(types, target), Converter(Context(types).Values, target));
    }

    [Fact]
    public void Converter_configuration_and_replaced_compilation_do_not_reuse_stale_results()
    {
        var compilation = CompilationFactory.Create(Model);
        var enabled = new RoslynTypeSystem(compilation);
        var disabled = new RoslynTypeSystem(compilation, new() { TypeConverterAttributes = ImmutableArray<string>.Empty });
        var token = enabled.Find("ConversionModel.Token")!;
        Assert.NotNull(Converter(Context(enabled).Values, token));
        Assert.Null(Converter(Context(disabled).Values, token));
        var replacement = new RoslynTypeSystem(CompilationFactory.Create(Model.Replace("typeof(Good)", "typeof(Other)")));
        Assert.Equal("Other", Converter(Context(replacement).Values, replacement.Find("ConversionModel.Token"))!.Name);
        Assert.Equal("Good", Converter(Context(enabled).Values, token)!.Name);
    }

    [Fact]
    public void Stateful_providers_and_conversion_rules_run_for_every_occurrence_after_cache_warmup()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var target = types.Find("ConversionModel.Token")!;
        Assert.Equal("Good", Converter(Context(types).Values, target)!.Name);
        var provider = new AlternatingProvider();
        var rule = new ObserveText();
        var profile = XamlFrameworkProfile.Portable with {
            TypeConverterProviders = ImmutableArray.Create<IXamlTypeConverterProvider>(provider),
            TextConversionRules = ImmutableArray.Create<IXamlTextConversionRule>(rule) };
        var context = Context(types, profile);
        for (var i = 0; i < 8; i++)
        {
            var expression = Assert.IsType<BoundConverterExpression>(context.Values.TryText("text" + i, target, NamespaceScope.Empty, new(i, 1)));
            Assert.Equal(i % 2 == 0 ? "Other" : "Good", expression.Converter.Name);
            Assert.Equal("text" + i, expression.Text);
            Assert.Equal(new TextSpan(i, 1), expression.Span);
        }
        Assert.Equal(8, provider.Calls);
        Assert.Equal(8, rule.Calls);
        Assert.Equal(8, context.Diagnostics.Count(d => d.Code == "PROVIDER"));
    }

    [Fact]
    public void Member_converter_still_precedes_rules_and_parse_but_expression_values_are_not_cached()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var rule = new ObserveText();
        var context = Context(types, XamlFrameworkProfile.Portable with { TextConversionRules = ImmutableArray.Create<IXamlTextConversionRule>(rule) });
        var property = (IPropertySymbol)types.Find("ConversionModel.Holder")!.GetMembers("Overridden").Single();
        for (var i = 0; i < 2; i++)
            Assert.IsType<BoundConverterExpression>(context.Values.TryText("override", property.Type, NamespaceScope.Empty, new(), property));
        Assert.Equal(0, rule.Calls);
        var first = Assert.IsType<BoundParseExpression>(context.Values.TryText("one", property.Type, NamespaceScope.Empty, new(1, 2)));
        var second = Assert.IsType<BoundParseExpression>(context.Values.TryText("two", property.Type, NamespaceScope.Empty, new(5, 3)));
        Assert.NotSame(first, second);
        Assert.Same(first.Method, second.Method);
        Assert.Equal("one", first.Text); Assert.Equal("two", second.Text);
        Assert.NotEqual(first.Span, second.Span);
        Assert.Equal(2, rule.Calls);
    }

    [Fact]
    public void Concurrent_selection_does_not_capture_context_or_duplicate_diagnostics()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var target = types.Find("ConversionModel.Pair")!;
        var token = types.Find("ConversionModel.Token")!;
        Parallel.For(0, 128, _ => {
            var context = Context(types);
            Assert.Same(PreviousParse(types, target), Parse(context.Values, target));
            Assert.Same(PreviousConverter(types, token), Converter(context.Values, token));
            Assert.Empty(context.Diagnostics);
        });
    }

    [Fact]
    public void Cancellation_on_cold_and_cached_paths_does_not_poison_later_selection()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var target = types.Find("ConversionModel.Pair")!;
        var token = types.Find("ConversionModel.Token")!;
        using var source = new CancellationTokenSource();
        var canceled = Context(types, cancellation: source.Token);
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => Parse(canceled.Values, target));
        Assert.Throws<OperationCanceledException>(() => Converter(canceled.Values, token));
        Assert.NotNull(Parse(Context(types).Values, target));
        Assert.NotNull(Converter(Context(types).Values, token));
        Assert.Throws<OperationCanceledException>(() => Parse(canceled.Values, target));
        Assert.Throws<OperationCanceledException>(() => Converter(canceled.Values, token));
    }

    [Fact]
    public void Holding_selected_symbols_does_not_retain_the_type_system_or_document()
    {
        var (owner, document, selected) = PopulateWeakEntries();
        for (var i = 0; i < 8 && (owner.IsAlive || document.IsAlive); i++) {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Assert.False(owner.IsAlive);
        Assert.False(document.IsAlive);
        GC.KeepAlive(selected);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Owner, WeakReference Document, ISymbol[] Selected) PopulateWeakEntries()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var context = Context(types);
        var selected = new ISymbol[] { Parse(context.Values, types.Find("ConversionModel.Pair")!)!, Converter(context.Values, types.Find("ConversionModel.Token"))! };
        return (new(types), new(context), selected);
    }

    // Frozen old predicates/order/iteration and string splitting, not calls into the candidate cache.
    private static IMethodSymbol? PreviousParse(RoslynTypeSystem types, ITypeSymbol target) => target.Members(ClrNames.Parse).OfType<IMethodSymbol>()
        .Where(m => m.IsStatic && !m.IsGenericMethod && types.IsAccessible(m) && m.Parameters.Length is 1 or 2 &&
            m.Parameters[0].Type.SpecialType == SpecialType.System_String && (m.Parameters.Length == 1 || m.Parameters[1].Type.HasMetadataName(ClrNames.IFormatProvider) || m.Parameters[1].Type.HasMetadataName(ClrNames.CultureInfo)) &&
            types.Compilation.ClassifyCommonConversion(m.ReturnType, target).IsImplicit)
        .OrderByDescending(m => m.Parameters.Length).FirstOrDefault();

    private static INamedTypeSymbol? PreviousConverter(RoslynTypeSystem types, ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes()) {
            if (attribute.AttributeClass == null || !types.Configuration.TypeConverterAttributes.Contains(attribute.AttributeClass.MetadataName())) continue;
            var value = attribute.ConstructorArguments.FirstOrDefault().Value;
            var type = value as INamedTypeSymbol ?? (value is string name ? types.Find(name.Split(',')[0].Trim()) : null);
            if (type == null || !types.IsAccessible(type) || !type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && types.IsAccessible(c))) continue;
            for (var current = type; current != null; current = current.BaseType)
                if (current.HasMetadataName(ClrNames.TypeConverter)) return type;
        }
        return null;
    }

    private sealed class AlternatingProvider : IXamlTypeConverterProvider
    {
        public int Calls;
        public INamedTypeSymbol? GetConverter(BindingContext context, ITypeSymbol targetType) {
            context.Report("PROVIDER", "call " + Calls, new(Calls, 0));
            return context.Types.Find(Calls++ % 2 == 0 ? "ConversionModel.Other" : "ConversionModel.NotAConverter");
        }
    }
    private sealed class ObserveText : IXamlTextConversionRule
    {
        public int Calls;
        public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope,
            TextSpan span, ISymbol? member, out BoundExpression? expression) { Calls++; expression = null; return false; }
    }
}
