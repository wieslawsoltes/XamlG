using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class MarkupAssignmentDagTests
{
    [Fact]
    public void Shared_null_expression_with_different_parameter_types_retains_original_lowering()
    {
        const string model = """
            using System;
            namespace DagCase;
            public class Root { public object Value { get; set; } }
            public class Extension
            {
                public Extension(object argument) { }
                public string Text { get; set; }
                public object ProvideValue(IServiceProvider provider) => this;
            }
            """;
        var profile = XamlFrameworkProfile.Portable with
        { BindingRules = ImmutableArray.Create<IXamlBindingRule>(new Rule()) };
        using var code = CompiledXaml.Create("<Root xmlns='clr-namespace:DagCase' S1='' S2=''/>", model, profile);
        Assert.DoesNotContain("__XamlGAssignMarkup_", code.Emission.Source, StringComparison.Ordinal);
        var root = code.Build();
        var extension = root.GetType().GetProperty("Value")!.GetValue(root)!;
        Assert.Null(extension.GetType().GetProperty("Text")!.GetValue(extension));
        Assert.True(XamlRuntimeSession.TryGet(root, out var session)); session!.Dispose();
    }

    private sealed class Rule : IXamlBindingRule
    {
        public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
        {
            if (attribute.Name is not ("S1" or "S2")) return false;
            var type = context.Types.Find("DagCase.Extension")!;
            var constructor = type.InstanceConstructors.Single(method => method.Parameters.Length == 1);
            var text = (IPropertySymbol)type.GetMembers("Text").Single();
            var destination = (IPropertySymbol)target.Type.GetMembers("Value").Single();
            var sharedNull = new BoundConstantExpression(null, context.Types.Special(SpecialType.System_Object), attribute.ValueSpan);
            var extension = new BoundObject(type, target.Syntax, scope, context.NewObjectKey(), null, "internal", constructor, null,
                ImmutableArray.Create<BoundExpression>(sharedNull),
                ImmutableArray.Create<BoundAssignment>(new BoundSetAssignment(new(text.Name, BoundMemberKind.Property, text,
                    text.Type, text.GetMethod, text.SetMethod, attribute.Span), sharedNull, attribute.Span)),
                false, false, false, target.NameScopeId);
            var method = (IMethodSymbol)type.GetMembers("ProvideValue").Single();
            target.Assignments.Add(new BoundSetAssignment(new(destination.Name, BoundMemberKind.Property, destination,
                destination.Type, destination.GetMethod, destination.SetMethod, attribute.Span),
                new BoundMarkupExpression(extension, method, method.ReturnType, attribute.ValueSpan), attribute.Span));
            return true;
        }
    }
}
