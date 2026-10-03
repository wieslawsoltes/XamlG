using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Bindings;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

/// <summary>Compiles Classes.name values and bindings through public framework contracts.</summary>
public sealed class AvaloniaClassBindingRule : IXamlBindingRule, IXamlObjectBindingRule
{
    public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target,
        XamlAttributeSyntax attribute, NamespaceScope scope)
    {
        var separator = attribute.Name.LastIndexOf('.');
        if (separator < 0) return false;
        var owner = context.ResolveType(attribute.Name.Substring(0, separator), scope, attribute.NameSpan, report: false);
        if (owner?.HasMetadataName(AvaloniaStyleMetadata.Classes) != true) return false;
        var styled = context.Types.Find(AvaloniaStyleMetadata.StyledElement);
        if (styled == null || !context.Types.Compilation.ClassifyCommonConversion(target.Type, styled).IsImplicit)
        {
            context.Report("XG3110", "Conditional classes require a StyledElement target.", attribute.NameSpan);
            return true;
        }
        var name = attribute.Name.Substring(separator + 1);
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith(":", StringComparison.Ordinal))
        {
            context.Report("XG3110", "A conditional class requires a non-pseudo class name.", attribute.NameSpan);
            return true;
        }
        var adapter = context.Types.Find(AvaloniaStyleMetadata.ClassAdapter)?.GetMembers("Apply")
            .OfType<IMethodSymbol>().FirstOrDefault(method => method.IsStatic && method.Parameters.Length == 3 && context.Types.IsAccessible(method));
        var descriptor = context.Types.Find(AvaloniaStyleMetadata.StyledElementExtensions)?.GetMembers(AvaloniaStyleMetadata.ClassPropertyFactory)
            .OfType<IMethodSymbol>().FirstOrDefault(method => method.IsStatic && method.Parameters.Length == 1 && context.Types.IsAccessible(method));
        if (adapter == null || descriptor == null)
        {
            context.Report("XG3002", "Conditional class compilation requires XamlG.AvaloniaRuntime and the public class-property contract.", attribute.Span);
            return true;
        }
        var boolean = context.Types.Special(SpecialType.System_Boolean);
        using var expected = new AvaloniaBindingTargetScope(target, boolean);
        var markup = attribute.Value.StartsWith("{", StringComparison.Ordinal) && !attribute.Value.StartsWith("{}", StringComparison.Ordinal);
        var value = context.Values.BindText(attribute.Value,
            markup ? context.Types.Special(SpecialType.System_Object) : boolean, scope, attribute.ValueSpan);
        if (value == null) return true;
        var className = new BoundConstantExpression(name, context.Types.Special(SpecialType.System_String), attribute.NameSpan);
        target.Assignments.Add(new BoundCallAssignment(adapter, ImmutableArray.Create<BoundExpression>(className, value), true, attribute.Span)
        {
            OwnResult = true,
            TargetDescriptor = new BoundCallExpression(descriptor, null, ImmutableArray.Create<BoundExpression>(className), attribute.NameSpan)
        });
        return true;
    }

    public void Initialize(BindingContext context, ObjectBindingBuilder target) { }

    public void Complete(BindingContext context, ObjectBindingBuilder target)
    {
        // A literal Classes list must be established before a conditional class removes an
        // entry, independently of XML attribute order.
        var conditional = target.Assignments.OfType<BoundCallAssignment>()
            .Where(call => call.Method.ContainingType.HasMetadataName(AvaloniaStyleMetadata.ClassAdapter)).ToArray();
        foreach (var call in conditional) target.Assignments.Remove(call);
        target.Assignments.AddRange(conditional);
    }
}
