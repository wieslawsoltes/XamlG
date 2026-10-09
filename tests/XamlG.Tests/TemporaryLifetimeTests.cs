using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class TemporaryLifetimeTests
{
    private const string Model = """
        using System;
        using System.Collections.Generic;
        using XamlG.Runtime;
        namespace Model {
          public class Panel {
            [Content] public List<Panel> Children { get; } = new();
            public string Text { get; set; }
            public object Value { get; set; }
            public List<object> Values { get; } = new();
            public Func<object> Callback { get; set; }
            public List<object> Seen { get; } = new();
            public int Subscribers { get; private set; }
            private EventHandler _pulse;
            public event EventHandler Pulse {
              add { _pulse += value; Subscribers++; }
              remove { _pulse -= value; Subscribers--; }
            }
            public void OnPulse(object sender, EventArgs args) => Seen.Add(sender);
            public void Raise() => _pulse?.Invoke(this, EventArgs.Empty);
          }
          public class CaptureExtension {
            public CaptureExtension(string text) { Text = text; }
            public string Text { get; }
            public object ProvideValue(IServiceProvider provider) => new Snapshot { Provider = provider, Text = Text };
          }
          public class Snapshot { public IServiceProvider Provider { get; set; } public string Text { get; set; } }
          public class Template {
            [Content, DeferredContent] public Func<IServiceProvider, object> Content { get; set; }
          }
        }
        """;

    private static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SafeSiblingsReuseLocalsWithoutChangingCapturedTargetsReferencesOrCleanup(bool project)
    {
        var children = string.Concat(Enumerable.Range(0, 24).Select(index =>
            "<Panel Text='text" + index + "' " + ((index % 4) switch
            {
                1 => "Pulse='OnPulse' Value='{Capture argument" + index + "}'",
                2 => "Capture='callback'",
                3 => "Value='{x:Reference final}'",
                _ => "Value='{Capture argument" + index + "}'"
            }) + "/>")) + "<Panel x:Name='final' Text='last'/>";
        var prefix = "<Panel xmlns='clr-namespace:Model' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' ";
        var profile = XamlFrameworkProfile.Portable with { BindingRules = ImmutableArray.Create<IXamlBindingRule>(new CaptureRule()) };
        using var pooled = CompiledXaml.Create(prefix + ">" + children + "</Panel>", Model, profile, shareAcrossDocuments: project);
        using var guarded = CompiledXaml.Create(prefix + "Guard='raw'>" + children + "</Panel>", Model, profile, shareAcrossDocuments: project);
        Assert.True(LocalSlots(pooled.Assembly) < LocalSlots(guarded.Assembly), "The safe sibling assignments should reuse local slots.");
        foreach (var code in new[] { pooled, guarded })
        {
            var root = code.Build();
            var built = (IList)Property(root, "Children")!;
            // Reference-containing assignments run when the namescope completes.
            // Identify nodes by their content rather than assuming insertion order.
            var byText = built.Cast<object>().ToDictionary(child => (string)Property(child, "Text")!);
            var seen = (IList)Property(root, "Seen")!;
            Assert.True(XamlRuntimeSession.TryGet(root, out var session));
            for (var index = 0; index < 24; index++)
            {
                var child = byText["text" + index];
                if (index % 4 == 3) Assert.Same(byText["last"], Property(child, "Value"));
                else
                {
                    var provider = index % 4 == 2
                        ? (IServiceProvider)((Delegate)Property(child, "Callback")!).DynamicInvoke()!
                        : (IServiceProvider)Property(Property(child, "Value")!, "Provider")!;
                    var target = Assert.IsAssignableFrom<IXamlProvideValueTarget>(provider.GetService(typeof(IXamlProvideValueTarget)));
                    Assert.Same(child, target.TargetObject);
                    Assert.Equal(index % 4 == 2 ? "Callback" : "Value", Assert.IsAssignableFrom<PropertyInfo>(target.TargetProperty).Name);
                }
                if (index % 4 == 1)
                {
                    Assert.Equal(1, Property(child, "Subscribers"));
                    child.GetType().GetMethod("Raise")!.Invoke(child, null);
                    Assert.Same(child, seen[seen.Count - 1]);
                }
                Assert.True(session!.Apply(index, [new XamlPropertyUpdate(session.FindNode(child)!.Key, "Text", "edited" + index)]).Applied);
            }
            for (var index = 0; index < 24; index++) Assert.Equal("edited" + index, Property(byText["text" + index], "Text"));
            Assert.Equal(6, seen.Count);
            session!.Dispose(); session.Dispose();
            foreach (var child in built.Cast<object>())
            {
                Assert.Equal(0, Property(child, "Subscribers"));
                child.GetType().GetMethod("Raise")!.Invoke(child, null);
            }
            Assert.Equal(6, seen.Count);
        }
    }

    private static int LocalSlots(Assembly assembly) => assembly.GetTypes().Sum(type =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Sum(method => method.GetMethodBody()?.LocalVariables.Count ?? 0));

    private sealed class CaptureRule : IXamlBindingRule
    {
        public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
        {
            if (attribute.Name == "Guard")
            {
                target.Assignments.Add(new BoundRawAssignment(";", attribute.Span));
                return true;
            }
            if (attribute.Name != "Capture") return false;
            var property = (IPropertySymbol)target.Type.GetMembers("Callback").Single();
            var member = new BoundMember(property.Name, BoundMemberKind.Property, property, property.Type,
                property.GetMethod, property.SetMethod, attribute.Span);
            var callback = new BoundLambdaExpression((INamedTypeSymbol)property.Type, ImmutableArray<BoundParameterExpression>.Empty,
                new BoundServiceExpression(context.Types.Find("System.IServiceProvider")!, attribute.Span), false, attribute.Span);
            target.Assignments.Add(new BoundSetAssignment(member, callback, attribute.Span));
            return true;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollectionDescriptorsRetainTheirOriginalOwnerFrames(bool project)
    {
        var children = string.Concat(Enumerable.Range(0, 8).Select(index =>
            "<Panel Text='owner" + index + "'><Panel.Values><CaptureExtension><x:Arguments><x:String>snapshot" + index +
            "</x:String></x:Arguments></CaptureExtension></Panel.Values></Panel>"));
        var profile = XamlFrameworkProfile.Portable with
            { MemberBindingRules = ImmutableArray.Create<IXamlMemberBindingRule>(new CollectionDescriptorRule()) };
        using var code = CompiledXaml.Create("<Panel xmlns='clr-namespace:Model' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" + children + "</Panel>", Model, profile, shareAcrossDocuments: project);
        var root = code.Build();
        var built = (IList)Property(root, "Children")!;
        for (var index = 0; index < built.Count; index++)
        {
            var owner = built[index]!;
            var snapshot = ((IList)Property(owner, "Values")!)[0]!;
            var provider = (IServiceProvider)Property(snapshot, "Provider")!;
            var target = Assert.IsAssignableFrom<IXamlProvideValueTarget>(provider.GetService(typeof(IXamlProvideValueTarget)));
            Assert.Same(owner, target.TargetObject);
            var captured = Assert.IsAssignableFrom<IServiceProvider>(Assert.IsAssignableFrom<Delegate>(target.TargetProperty).DynamicInvoke());
            var parents = Assert.IsAssignableFrom<IXamlParentStackProvider>(captured.GetService(typeof(IXamlParentStackProvider)));
            Assert.Same(owner, parents.Parents.First());
            Assert.Equal("snapshot" + index, Property(snapshot, "Text"));
        }
        Assert.True(XamlRuntimeSession.TryGet(root, out var session)); session!.Dispose();
    }

    private sealed class CollectionDescriptorRule : IXamlMemberBindingRule
    {
        public BoundMember Bind(BindingContext context, ITypeSymbol targetType, BoundMember member, NamespaceScope scope)
        {
            if (member.Name != "Values") return member;
            var function = context.Types.Find("System.Func`1")!.Construct(context.Types.Find("System.IServiceProvider")!);
            return member with { TargetDescriptor = new BoundLambdaExpression(function, ImmutableArray<BoundParameterExpression>.Empty,
                new BoundServiceExpression(context.Types.Find("System.IServiceProvider")!, member.Span), false, member.Span) };
        }
    }

    [Fact]
    public void RetainedProvidersAndLiveSettersKeepEachSiblingTarget()
    {
        var children = string.Concat(Enumerable.Range(0, 24).Select(index =>
            "<Panel Text='text" + index + "' Value='{Capture argument" + index + "}'/>"));
        using var code = CompiledXaml.Create("<Panel xmlns='clr-namespace:Model'>" + children + "</Panel>", Model);
        var root = code.Build();
        var built = (IList)Property(root, "Children")!;
        Assert.True(XamlRuntimeSession.TryGet(root, out var session));
        for (var index = 0; index < built.Count; index++)
        {
            var child = built[index]!;
            var snapshot = Property(child, "Value")!;
            Assert.Equal("argument" + index, Property(snapshot, "Text"));
            var provider = (IServiceProvider)Property(snapshot, "Provider")!;
            var target = Assert.IsAssignableFrom<IXamlProvideValueTarget>(provider.GetService(typeof(IXamlProvideValueTarget)));
            Assert.Same(child, target.TargetObject);
            Assert.Equal("Value", Assert.IsAssignableFrom<System.Reflection.PropertyInfo>(target.TargetProperty).Name);
            var parents = Assert.IsAssignableFrom<IXamlParentStackProvider>(provider.GetService(typeof(IXamlParentStackProvider)));
            Assert.Contains(child, parents.Parents);
            Assert.Contains(root, parents.Parents);
            Assert.True(session!.Apply(index, new[] { new XamlPropertyUpdate(session.FindNode(child)!.Key, "Text", "edited" + index) }).Applied);
        }
        for (var index = 0; index < built.Count; index++) Assert.Equal("edited" + index, Property(built[index]!, "Text"));
    }

    [Fact]
    public void CapturedDeferredScopesKeepTheirOriginalParents()
    {
        const string child = "<Panel><Panel.Value><Template><Panel Value='{Capture later}'/></Template></Panel.Value></Panel>";
        using var code = CompiledXaml.Create("<Panel xmlns='clr-namespace:Model'>" + child + child + "</Panel>", Model);
        var root = code.Build();
        var siblings = (IList)Property(root, "Children")!;
        foreach (var sibling in siblings)
        {
            var template = Property(sibling!, "Value")!;
            var factory = (Delegate)Property(template, "Content")!;
            for (var invocation = 0; invocation < 2; invocation++)
            {
                var content = factory.DynamicInvoke(new object?[] { null })!;
                var snapshot = Property(content, "Value")!;
                var provider = (IServiceProvider)Property(snapshot, "Provider")!;
                var parents = Assert.IsAssignableFrom<IXamlParentStackProvider>(provider.GetService(typeof(IXamlParentStackProvider)));
                Assert.Contains(sibling, parents.Parents);
                Assert.DoesNotContain(siblings.Cast<object>().Single(other => !ReferenceEquals(other, sibling)), parents.Parents);
            }
        }
    }
}
