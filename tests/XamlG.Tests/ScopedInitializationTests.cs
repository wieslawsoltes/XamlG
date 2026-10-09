using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ScopedInitializationTests
{
    private const string Model = """
        using System;
        using System.Collections.Generic;
        using XamlG.Runtime;
        namespace Scopes;
        public class View {
            public static List<string> Log { get; } = new();
            public static string Fail;
            public int Value { get; set; }
            public static int Read() { Log.Add("read"); if (Fail == "read") throw new Exception("read"); return 3; }
        }
        public class Owner {
            internal int Count;
            public Owner() { View.Log.Add("construct"); }
            public Scope Open() { View.Log.Add("open"); if (View.Fail == "open") throw new Exception("open"); return new Scope(this); }
            public int Result { get { View.Log.Add("result"); return Count; } }
        }
        public TYPE Scope : IDisposable {
            private readonly Owner _owner;
            private int _count;
            public Scope(Owner owner) { _owner = owner; _count = 0; }
            public void Add(int count) { _count += count; View.Log.Add("add"); if (View.Fail == "add") throw new Exception("add"); }
            public void Check(IServiceProvider services) {
                var target = (IXamlProvideValueTarget)services.GetService(typeof(IXamlProvideValueTarget));
                if (target.TargetObject is not View || !Equals(target.TargetProperty, "Value")) throw new Exception("target");
                View.Log.Add("check:" + _count);
            }
            public void Dispose() { _owner.Count = _count; View.Log.Add("dispose:" + _count); if (View.Fail == "dispose") throw new Exception("dispose"); }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopedInitializationPreservesMutationServicesOrderAndFailureDisposal(bool valueType)
    {
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Scopes' Value='test'/>",
            Model.Replace("TYPE", valueType ? "struct" : "class", StringComparison.Ordinal),
            XamlFrameworkProfile.Portable with { BindingRules = ImmutableArray.Create<IXamlBindingRule>(new Rule()) });
        var view = code.Assembly.GetType("Scopes.View")!;
        var log = Assert.IsType<List<string>>(view.GetProperty("Log")!.GetValue(null));
        var root = code.Build();
        Assert.Equal(3, view.GetProperty("Value")!.GetValue(root));
        Assert.Equal(["construct", "open", "read", "add", "check:3", "dispose:3", "result"], log);
        foreach (var failure in new[] { "open", "read", "add", "dispose" })
        {
            log.Clear(); view.GetField("Fail")!.SetValue(null, failure);
            var error = Assert.Throws<TargetInvocationException>(() => code.Build());
            Assert.Equal(failure, error.InnerException!.Message);
            Assert.DoesNotContain("result", log);
            if (failure == "open") Assert.Equal(["construct", "open"], log);
            else Assert.EndsWith(failure == "read" ? "dispose:0" : "dispose:3", string.Join(",", log), StringComparison.Ordinal);
        }
    }

    private sealed class Rule : IXamlBindingRule
    {
        public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
        {
            if (attribute.Name != "Value") return false;
            var owner = context.Types.Find("Scopes.Owner")!;
            var builder = context.Types.Find("Scopes.Scope")!;
            var span = attribute.Span;
            var value = new BoundScopedInitializationExpression(new BoundNewExpression(owner.InstanceConstructors.Single(), [], span),
                (IMethodSymbol)owner.GetMembers("Open").Single(), ImmutableArray.Create(
                    new BoundBuilderCall((IMethodSymbol)builder.GetMembers("Add").Single(), ImmutableArray.Create<BoundExpression>(
                        new BoundCallExpression((IMethodSymbol)target.Type.GetMembers("Read").Single(), null, [], span))),
                    new BoundBuilderCall((IMethodSymbol)builder.GetMembers("Check").Single(), ImmutableArray.Create<BoundExpression>(
                        new BoundServiceExpression(context.Types.Find("System.IServiceProvider")!, span)))),
                (IPropertySymbol)owner.GetMembers("Result").Single(), span);
            var property = (IPropertySymbol)target.Type.GetMembers("Value").Single();
            target.Assignments.Add(new BoundSetAssignment(new(property.Name, BoundMemberKind.Property, property,
                property.Type, property.GetMethod, property.SetMethod, span)
                { TargetDescriptor = new BoundConstantExpression("Value", context.Types.Special(SpecialType.System_String), span) }, value, span));
            return true;
        }
    }
}
