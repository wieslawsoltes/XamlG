using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.Syntax;

namespace XamlG.Tooling;

public static class XamlInspector
{
    public static ImmutableArray<XamlInspectionNode> Syntax(XamlSyntaxTree tree) =>
        tree.Nodes.Select((node, index) => SyntaxNode(node, index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToImmutableArray();
    public static XamlInspectionNode? Bound(BoundDocument document) => document.Root == null ? null : Object(document.Root);

    private static XamlInspectionNode SyntaxNode(XamlSyntaxNode node, string id)
    {
        if (node is XamlElementSyntax element)
        {
            var attributes = element.Attributes.Select((a, i) => new XamlInspectionNode(id + "/a" + i,
                a.IsNamespace ? "Namespace" : "Attribute", a.Name + " = " + a.Value, a.Span, null, ImmutableArray<XamlInspectionNode>.Empty));
            var children = element.Children.Select((child, i) => SyntaxNode(child, id + "/" + i));
            return new(id, "Element", element.Name, element.Span, null, attributes.Concat(children).ToImmutableArray());
        }
        return new(id, node is XamlTextSyntax t && t.IsCData ? "CDATA" : node is XamlTextSyntax ? "Text" : "Trivia",
            node is XamlTextSyntax text ? text.Value : ((XamlTriviaSyntax)node).Kind, node.Span, null, ImmutableArray<XamlInspectionNode>.Empty);
    }
    private static XamlInspectionNode Object(BoundObject value)
    {
        var children = value.Arguments.Select((a, i) => Expression(a, value.Key + "/arg" + i))
            .Concat(value.Assignments.Select((a, i) => Assignment(a, value.Key + "/set" + i))).ToImmutableArray();
        return new(value.Key, "Object", value.Name ?? value.Type.Name, value.Syntax.Span, value.Type.ToDisplayString(), children);
    }
    private static XamlInspectionNode Assignment(BoundAssignment value, string id)
    {
        var label = value switch
        {
            BoundSetAssignment s => s.Member.Name,
            BoundAddAssignment a => a.Collection?.Name ?? a.AddMethod.Name,
            BoundEventAssignment e => e.Event.Name + " += " + e.HandlerName,
            BoundDynamicSetAssignment d => d.Target.Name,
            BoundAdaptedSetAssignment a => a.Member.Name + (a.OwnAdapterResult ? " (owned subscription)" : string.Empty),
            BoundCallAssignment c => c.Method.Name,
            _ => value.GetType().Name
        };
        var expressions = value switch
        {
            BoundSetAssignment s => ImmutableArray.Create(s.Value),
            BoundAddAssignment a => a.Arguments,
            BoundEventAssignment { Value: { } handler } => ImmutableArray.Create(handler),
            BoundDynamicSetAssignment d => ImmutableArray.Create(d.Value),
            BoundAdaptedSetAssignment a => ImmutableArray.Create(a.Value),
            BoundCallAssignment c => c.Arguments,
            _ => ImmutableArray<BoundExpression>.Empty
        };
        return new(id, value.GetType().Name, label, value.Span, null,
            expressions.Select((e, i) => Expression(e, id + "/" + i)).ToImmutableArray());
    }
    /// <summary>Inspects typed delegates and resource references without executing their factories.</summary>
    public static XamlInspectionNode Expression(BoundExpression value, string id = "expression")
    {
        if (value is BoundObjectExpression obj) return Object(obj.Object);
        if (value is BoundMarkupExpression extension)
            return new(id, "MarkupExtension", extension.Method.Name, value.Span, value.Type?.ToDisplayString(), ImmutableArray.Create(Object(extension.Extension)));
        var label = value switch
        {
            BoundResourceExpression r => r.Resource.Uri + (r.Resource.ExternalFactory == null ? " (project factory)" : " (referenced factory)"),
            BoundConstantExpression c => c.Value is IFormattable formatted ? formatted.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : c.Value?.ToString() ?? "null",
            BoundTypeExpression t => t.ReferencedType.ToDisplayString(),
            BoundReferenceExpression r => r.Name,
            BoundStaticExpression s => s.Member.ToDisplayString(),
            BoundParseExpression p => p.Text,
            BoundConverterExpression c => c.Text,
            BoundValueConverterExpression c => c.Converter.ToDisplayString(),
            BoundParameterExpression p => p.Name,
            BoundLambdaExpression l => (l.IsStatic ? "static " : string.Empty) + "(" + string.Join(", ", l.Parameters.Select(p => p.Name)) + ") =>",
            BoundPropertyAccessExpression p => p.Property.ToDisplayString(),
            BoundFieldAccessExpression f => f.Field.ToDisplayString(),
            BoundMethodGroupExpression m => m.Method.ToDisplayString(),
            BoundAssignmentExpression => "Store",
            BoundCallExpression c => c.Method.ToDisplayString(),
            _ => value.GetType().Name
        };
        var children = value switch
        {
            BoundChoiceExpression c => ImmutableArray.Create<BoundExpression>(new BoundObjectExpression(c.Extension)).AddRange(c.Branches.SelectMany(b => new[] { b.Option, b.Value })).AddRange(c.Default == null ? Enumerable.Empty<BoundExpression>() : new[] { c.Default }),
            BoundCastExpression c => ImmutableArray.Create(c.Value),
            BoundValueConverterExpression c => ImmutableArray.Create(c.Value),
            BoundArrayExpression a => a.Values,
            BoundNewExpression n => n.Arguments,
            BoundCallExpression c => c.Receiver == null ? c.Arguments : c.Arguments.Insert(0, c.Receiver),
            BoundDeferredExpression d => ImmutableArray.Create(d.Content),
            BoundLambdaExpression l => l.Parameters.Cast<BoundExpression>().Append(l.Body).ToImmutableArray(),
            BoundPropertyAccessExpression p => p.IndexArguments.Insert(0, p.Receiver),
            BoundFieldAccessExpression f => ImmutableArray.Create(f.Receiver),
            BoundAssignmentExpression a => ImmutableArray.Create(a.Target, a.Value),
            BoundMethodGroupExpression m when m.Receiver != null => ImmutableArray.Create(m.Receiver),
            _ => ImmutableArray<BoundExpression>.Empty
        };
        return new(id, value.GetType().Name, label, value.Span, value.Type?.ToDisplayString(),
            children.Select((e, i) => Expression(e, id + "/" + i)).ToImmutableArray());
    }
}
