using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class InstanceMemberCacheTests
{
    private const string Model = """
        using System;
        namespace Fixture {
            public class Base<T> {
                public T Value { get; set; } = default!;
                public string Shared { get; set; } = "";
                public string Restricted { get; private set; } = "";
                protected string Protected { get; set; } = "";
                private string Private { get; set; } = "";
                public static string Static { get; set; } = "";
                public string this[int index] => "";
                public event Action Changed { add {} remove {} }
                private event Action PrivateEvent { add {} remove {} }
                public class Nested { }
            }
            public class Derived : Base<string> {
                public new event Action Shared { add {} remove {} }
                public new static string Value { get; set; } = "";
                public string Own { get; set; } = "";
            }
            public class Other { }
            public struct ValueOwner { public int Value { get; set; } }
            public interface IRoot { string Item { get; set; } event Action Changed; }
            public interface ILeft : IRoot { new int Item { get; set; } }
            public interface IRight : IRoot { }
            public interface IDiamond : ILeft, IRight { }
        }
        """;
    private static readonly XamlSyntaxTree Syntax = XamlSyntaxTree.Parse("<Root xmlns='clr-namespace:Fixture'/>");
    private static readonly NamespaceScope Scope = NamespaceScope.Empty.Push(Syntax.Root!);
    private static BindingContext Context(RoslynTypeSystem types, CancellationToken cancellation = default,
        XamlFrameworkProfile? profile = null) => BindingContext.CreateSignatureProbe(Syntax, types,
            profile ?? XamlFrameworkProfile.Portable, new(), cancellation);

    // Frozen pre-cache selection: property-first across the whole inheritance
    // traversal, then event-first in that same order. Accessibility is not mocked.
    private static ISymbol? Legacy(RoslynTypeSystem types, ITypeSymbol target, string name, INamedTypeSymbol? within) =>
        (ISymbol?)target.Members(name).OfType<IPropertySymbol>().FirstOrDefault(p => !p.IsStatic && !p.IsIndexer && types.IsAccessible(p, within)) ??
        target.Members(name).OfType<IEventSymbol>().FirstOrDefault(e => !e.IsStatic && types.IsAccessible(e, within));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cold_and_warm_results_match_the_frozen_selector_for_each_access_context(bool parallel)
    {
        var compilation = CompilationFactory.Create(Model);
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var types = new RoslynTypeSystem(compilation);
        var open = compilation.GetTypeByMetadataName("Fixture.Base`1")!;
        var closed = open.Construct(compilation.GetSpecialType(SpecialType.System_String));
        var derived = compilation.GetTypeByMetadataName("Fixture.Derived")!;
        var targets = new ITypeSymbol[] { open, closed, open.Construct(compilation.GetSpecialType(SpecialType.System_Int32)), derived,
            compilation.GetTypeByMetadataName("Fixture.IDiamond")!, compilation.GetTypeByMetadataName("Fixture.ValueOwner")!, open.TypeParameters[0] };
        var contexts = new INamedTypeSymbol?[] { null, open, closed, derived,
            compilation.GetTypeByMetadataName("Fixture.Other"), open.GetTypeMembers("Nested").Single() };
        var names = new[] { "Value", "Shared", "Restricted", "Protected", "Private", "Static", "Item", "Changed", "PrivateEvent", "Own", "Missing", "value" };
        var cases = targets.SelectMany(target => contexts.SelectMany(within => names.Select(name =>
            (target, within, name, expected: Legacy(types, target, name, within))))).ToArray();
        void Check(int _)
        {
            var context = Context(types);
            foreach (var item in cases)
            {
                context.RootClass = item.within;
                var actual = context.Members.Resolve(item.target, item.name, Scope, new(7, 3), report: false);
                Assert.True(SymbolEqualityComparer.Default.Equals(item.expected, actual?.Symbol), item.target + ":" + item.name);
                if (actual != null) Assert.Equal(new TextSpan(7, 3), actual.Span);
                if (actual?.Symbol is IPropertySymbol property)
                {
                    Assert.True(SymbolEqualityComparer.Default.Equals(property.Type, actual.ValueType));
                    Assert.Equal(property.GetMethod != null && types.IsAccessible(property.GetMethod, item.within), actual.Getter != null);
                    Assert.Equal(property.SetMethod != null && types.IsAccessible(property.SetMethod, item.within), actual.Setter != null);
                }
            }
        }
        if (parallel) Parallel.For(0, 8, Check); else { Check(0); Check(1); }
    }

    [Fact]
    public void Property_priority_and_qualified_owner_checks_are_unchanged()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var context = Context(types);
        var target = types.Find("Fixture.Derived")!;
        var ordinary = context.Members.Resolve(target, "Shared", Scope, new(0, 1))!;
        Assert.IsAssignableFrom<IPropertySymbol>(ordinary.Symbol);
        Assert.Equal("Base", ordinary.Symbol.ContainingType.Name);
        var qualified = context.Members.Resolve(target, "Derived.Shared", Scope, new(3, 1))!;
        Assert.True(SymbolEqualityComparer.Default.Equals(ordinary.Symbol, qualified.Symbol));
        Assert.Null(context.Members.Resolve(target, "Other.Shared", Scope, new(5, 1), report: false));
        Assert.Empty(context.Diagnostics);
    }

    [Fact]
    public void Bound_members_rules_symbols_and_negative_diagnostics_remain_occurrence_local()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var rule = new Observe();
        var profile = XamlFrameworkProfile.Portable with { MemberBindingRules = ImmutableArray.Create<IXamlMemberBindingRule>(rule) };
        var first = Context(types, profile: profile);
        var second = Context(types, profile: profile);
        var target = types.Find("Fixture.Derived")!;
        var left = first.Members.Resolve(target, "Own", Scope, new(2, 3))!;
        var right = second.Members.Resolve(target, "Own", Scope, new(9, 3))!;
        Assert.NotSame(left, right);
        Assert.Equal(2, rule.Calls);
        Assert.False(left.AllowRepeatedAssignments);
        Assert.True(right.AllowRepeatedAssignments);
        Assert.Equal(new TextSpan(2, 3), Assert.Single(first.Symbols).Span);
        Assert.Equal(new TextSpan(9, 3), Assert.Single(second.Symbols).Span);
        Assert.Equal(new TextSpan(2, 3), Assert.Single(first.Diagnostics).Span);
        Assert.Equal(new TextSpan(9, 3), Assert.Single(second.Diagnostics).Span);
        first.Diagnostics.Clear();
        Assert.Null(first.Members.Resolve(target, "Missing", Scope, new(1, 1), report: false));
        Assert.Empty(first.Diagnostics);
        Assert.Null(first.Members.Resolve(target, "Missing", Scope, new(4, 2)));
        Assert.Null(first.Members.Resolve(target, "Missing", Scope, new(8, 2)));
        Assert.Equal(new[] { new TextSpan(4, 2), new TextSpan(8, 2) }, first.Diagnostics.Select(d => d.Span));
        Assert.All(first.Diagnostics, d => Assert.Equal("XG1005", d.Code));
    }

    [Fact]
    public void Changed_compilations_and_closed_generics_cannot_reuse_stale_symbols()
    {
        var firstTypes = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var nextTypes = new RoslynTypeSystem(CompilationFactory.Create(Model.Replace("public string Own", "private string Own")));
        var first = Context(firstTypes); var next = Context(nextTypes);
        Assert.NotNull(first.Members.Resolve(firstTypes.Find("Fixture.Derived")!, "Own", Scope, default));
        Assert.Null(next.Members.Resolve(nextTypes.Find("Fixture.Derived")!, "Own", Scope, default, report: false));
        var generic = firstTypes.Find("Fixture.Base`1")!;
        var text = first.Members.Resolve(generic.Construct(firstTypes.Special(SpecialType.System_String)), "Value", Scope, default)!;
        var number = first.Members.Resolve(generic.Construct(firstTypes.Special(SpecialType.System_Int32)), "Value", Scope, default)!;
        Assert.Equal(SpecialType.System_String, text.ValueType.SpecialType);
        Assert.Equal(SpecialType.System_Int32, number.ValueType.SpecialType);
        Assert.NotNull(first.Members.Resolve(firstTypes.Find("Fixture.Derived")!, "Own", Scope, default));
    }

    [Fact]
    public void Nullable_annotations_and_tuple_element_names_remain_distinct()
    {
        var source = "#nullable enable\n" + Model + """
            namespace Fixture {
                public class TupleA : Base<(int Left, int Right)> { }
                public class TupleB : Base<(int X, int Y)> { }
                public class NullableA : Base<string> { }
                public class NullableB : Base<string?> { }
            }
            """;
        var types = new RoslynTypeSystem(CompilationFactory.Create(source));
        var context = Context(types);
        foreach (var name in new[] { "TupleA", "TupleB", "NullableA", "NullableB", "TupleB", "NullableA" })
        {
            var target = types.Find("Fixture." + name)!.BaseType!;
            var property = (IPropertySymbol)Legacy(types, target, "Value", null)!;
            var actual = context.Members.Resolve(target, "Value", Scope, default)!;
            Assert.True(SymbolEqualityComparer.IncludeNullability.Equals(property, actual.Symbol));
            Assert.True(SymbolEqualityComparer.IncludeNullability.Equals(property.Type, actual.ValueType));
            Assert.Equal(property.Type.NullableAnnotation, actual.ValueType.NullableAnnotation);
            if (property.Type is INamedTypeSymbol { IsTupleType: true } tuple)
                Assert.Equal(tuple.TupleElements.Select(field => field.Name),
                    ((INamedTypeSymbol)actual.ValueType).TupleElements.Select(field => field.Name));
        }
    }

    [Fact]
    public void Saturation_and_long_names_fall_back_without_altering_selection()
    {
        var longName = new string('N', 300);
        var types = new RoslynTypeSystem(CompilationFactory.Create("public class Item { public int Value {get;set;} public int " + longName + " {get;set;} }"));
        var context = Context(types); var target = types.Find("Item")!;
        Assert.NotNull(context.Members.Resolve(target, longName, Scope, default));
        Assert.Equal(0, Count(context.Members));
        var value = context.Members.Resolve(target, "Value", Scope, default)!;
        Parallel.For(0, 8, worker =>
        {
            var local = Context(types);
            for (var i = 0; i < 600; i++) Assert.Null(local.Members.Resolve(target, "Missing" + (worker * 600 + i), Scope, default, report: false));
        });
        Assert.Equal(4096, Count(context.Members));
        Assert.Same(value.Symbol, context.Members.Resolve(target, "Value", Scope, default)!.Symbol);
        Assert.NotNull(context.Members.Resolve(target, longName, Scope, default));
        Assert.Null(context.Members.Resolve(target, "AnotherMissing", Scope, default, report: false));
        Assert.Equal(4096, Count(context.Members));
    }

    [Fact]
    public void Cancellation_is_checked_on_hits_and_does_not_poison_other_contexts()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        using var cancellation = new CancellationTokenSource();
        var context = Context(types, cancellation.Token); var target = types.Find("Fixture.Derived")!;
        Assert.NotNull(context.Members.Resolve(target, "Own", Scope, default));
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => context.Members.Resolve(target, "Own", Scope, default));
        Assert.Throws<OperationCanceledException>(() => context.Members.Resolve(target, "Missing", Scope, default));
        Assert.Equal(1, Count(context.Members));
        Assert.NotNull(Context(types).Members.Resolve(target, "Own", Scope, default));
    }

    [Fact]
    public void Weak_cache_does_not_retain_documents_type_systems_or_compilations()
    {
        var references = Populate();
        for (var i = 0; i < 12 && references.Any(reference => reference.IsAlive); i++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(references, reference => Assert.False(reference.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] Populate()
    {
        var compilation = CompilationFactory.Create(Model);
        var types = new RoslynTypeSystem(compilation); var context = Context(types);
        Assert.NotNull(context.Members.Resolve(types.Find("Fixture.Derived")!, "Own", Scope, default));
        return new[] { new WeakReference(context), new WeakReference(types), new WeakReference(compilation) };
    }

    private static int Count(MemberBinder binder)
    {
        var cache = typeof(MemberBinder).GetField("_instanceMembers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(binder)!;
        return (int)cache.GetType().GetField("_count", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
    }

    private sealed class Observe : IXamlMemberBindingRule
    {
        public int Calls;
        public BoundMember Bind(BindingContext context, ITypeSymbol targetType, BoundMember member, NamespaceScope scope)
        {
            Calls++;
            context.Report("OBSERVE", "occurrence", member.Span, XamlSeverity.Warning);
            return member with { AllowRepeatedAssignments = Calls % 2 == 0 };
        }
    }
}
