using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class AssignmentMemberNameTests
{
    private const string Model = """
        #nullable enable
        namespace Fixture {
            public class Box<T> {
                public T Value { get; set; } = default!;
                public string @event { get; set; } = "";
                public static void SetTag(object target, string value) { }
                public class Nested<U> { public (T Left, U Right) Pair { get; set; } }
            }
        }
        """;
    private static readonly XamlSyntaxTree Syntax = XamlSyntaxTree.Parse("<Root/>");
    private static BindingContext Context(RoslynTypeSystem types) => BindingContext.CreateSignatureProbe(
        Syntax, types, XamlFrameworkProfile.Portable, new());
    private static ObjectBindingBuilder Owner(INamedTypeSymbol type) => new(type, Syntax.Root!, NamespaceScope.Empty, "n0", true, 0);
    private static BoundMember Member(ISymbol symbol) => symbol is IPropertySymbol property
        ? new(property.Name, BoundMemberKind.Property, property, property.Type, property.GetMethod, property.SetMethod, default)
        : new(symbol.Name, BoundMemberKind.AttachedProperty, symbol, ((IMethodSymbol)symbol).Parameters[1].Type, null, (IMethodSymbol)symbol, default);

    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    public void Textual_identity_matches_legacy_formatting_for_every_symbol_and_culture(string culture)
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var open = types.Find("Fixture.Box`1")!;
        var closed = open.Construct(types.Special(SpecialType.System_String));
        var nested = closed.GetTypeMembers("Nested").Single().Construct(types.Special(SpecialType.System_Int32));
        var symbols = open.GetMembers().Concat(closed.GetMembers()).Concat(nested.GetMembers())
            .Where(symbol => symbol is IPropertySymbol || symbol is IMethodSymbol { Name: "SetTag" }).ToArray();
        // Populate once, then query under the selected culture with fresh owners.
        var saved = CultureInfo.CurrentCulture; var savedUi = CultureInfo.CurrentUICulture;
        try
        {
            var context = Context(types);
            for (var round = 0; round < 2; round++)
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(round == 0 ? "en-US" : culture);
                foreach (var symbol in symbols)
                {
                    var owner = Owner(closed); var member = Member(symbol);
                    var value = new BoundConstantExpression(null, null, default);
                    context.Members.AddSet(owner, member, value, new(2, 1));
                    Assert.Equal(symbol.ToDisplayString(), Assert.Single(owner.AssignedScalars));
                    context.Members.AddSet(owner, member, value, new(4, 1));
                    Assert.Single(owner.Assignments);
                    Assert.Equal("XG1014", context.Diagnostics[context.Diagnostics.Count - 1].Code);
                    Assert.Equal(new TextSpan(4, 1), context.Diagnostics[context.Diagnostics.Count - 1].Span);
                }
            }
        }
        finally { CultureInfo.CurrentCulture = saved; CultureInfo.CurrentUICulture = savedUi; }
    }

    [Fact]
    public void Different_symbols_with_identical_legacy_text_still_collide()
    {
        var first = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var second = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var firstProperty = first.Find("Fixture.Box`1")!.GetMembers("Value").Single();
        var secondProperty = second.Find("Fixture.Box`1")!.GetMembers("Value").Single();
        Assert.NotSame(firstProperty, secondProperty);
        Assert.Equal(firstProperty.ToDisplayString(), secondProperty.ToDisplayString());
        var context = Context(first); var owner = Owner(first.Find("Fixture.Box`1")!);
        var value = new BoundConstantExpression(null, null, default);
        context.Members.AddSet(owner, Member(firstProperty), value, new(1, 1));
        context.Members.AddSet(owner, Member(secondProperty), value, new(3, 1));
        Assert.Single(owner.Assignments);
        Assert.Equal("XG1014", Assert.Single(context.Diagnostics).Code);
        Assert.Equal(new TextSpan(3, 1), context.Diagnostics[0].Span);
        context.Members.AddSet(owner, Member(secondProperty) with { AllowRepeatedAssignments = true }, value, new(5, 1));
        Assert.Equal(2, owner.Assignments.Count);
        Assert.Single(owner.AssignedScalars);
        Assert.Single(context.Diagnostics);
    }

    [Fact]
    public void Independent_targets_and_parallel_contexts_keep_separate_assignment_sets()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var target = types.Find("Fixture.Box`1")!.Construct(types.Special(SpecialType.System_String));
        var member = Member(target.GetMembers("Value").Single());
        Parallel.For(0, 32, _ =>
        {
            var context = Context(types);
            for (var i = 0; i < 50; i++)
            {
                var owner = Owner(target);
                context.Members.AddSet(owner, member, new BoundConstantExpression(null, null, default), default);
                Assert.Single(owner.Assignments);
                Assert.Single(owner.AssignedScalars);
            }
            Assert.Empty(context.Diagnostics);
        });
    }

    [Fact]
    public void Formatted_names_do_not_keep_the_source_compilation_alive()
    {
        var weak = Populate();
        for (var i = 0; i < 12 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Populate()
    {
        var compilation = CompilationFactory.Create(Model);
        var types = new RoslynTypeSystem(compilation); var context = Context(types);
        var target = types.Find("Fixture.Box`1")!;
        context.Members.AddSet(Owner(target), Member(target.GetMembers("Value").Single()), new BoundConstantExpression(null, null, default), default);
        return new WeakReference(compilation);
    }
}
