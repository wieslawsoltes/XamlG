using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal static class AvaloniaDataTypeMetadata
{
    public static readonly XamlAnnotationKey<IPropertySymbol> MappedDirective = new("Avalonia.DataTypeProperty");
    private static readonly string[] Attributes = { AvaloniaBindingMetadata.DataTypeAttribute };

    public static (bool HasDirective, ITypeSymbol? Type) Read(BindingContext context, ObjectBindingBuilder target)
    {
        var values = BindingPropertyValue.Read(context, target).ToArray();
        var properties = target.Type.Members().OfType<IPropertySymbol>().Where(property => property.HasAttribute(Attributes)).ToArray();
        var directive = target.Scope.Directive(target.Syntax, AvaloniaBindingMetadata.DataType);
        var mapped = directive != null && !values.Any(value => value.MemberName == AvaloniaBindingMetadata.DataType)
            ? properties.FirstOrDefault(property => property.Name == AvaloniaBindingMetadata.DataType) : null;
        if (directive != null && mapped == null)
        {
            var type = StaticType(context, new XamlTextSyntax(directive.Value, false, directive.ValueSpan), target.Scope, target.NameScopeId);
            if (type == null) context.Report("XG3213", "x:DataType requires a statically resolved type name.", directive.ValueSpan);
            return (true, type);
        }
        if (mapped != null) target.Annotations.Set(MappedDirective, mapped);
        ITypeSymbol? inferred = null;
        foreach (var value in values)
        {
            var expanded = value.Scope.Expand(value.Name, true);
            var isDirective = expanded.Namespace != null && XamlNames.IsLanguage(expanded.Namespace) && expanded.LocalName == AvaloniaBindingMetadata.DataType;
            if (!isDirective)
            {
                if (!properties.Any(property => property.Name == value.MemberName)) continue;
                var member = context.Members.Resolve(target.Type, value.Name, value.Scope, value.NameSpan, report: false);
                if (member?.Symbol is not IPropertySymbol property || !property.HasAttribute(Attributes)) continue;
            }
            else if (mapped == null) continue;
            var nodes = value.Values.Where(node => node is XamlElementSyntax || node is XamlTextSyntax text && !string.IsNullOrWhiteSpace(text.Value)).ToArray();
            if (nodes.Length == 1 && StaticType(context, nodes[0], value.Scope, target.NameScopeId) is { } type) inferred = type;
        }
        return (false, inferred);
    }

    private static ITypeSymbol? StaticType(BindingContext context, XamlSyntaxNode value, NamespaceScope scope, int nameScope)
    {
        if (value is XamlTextSyntax text)
        {
            var input = text.Value.Trim();
            if (!input.StartsWith("{", StringComparison.Ordinal))
                return context.Values.ResolveTypeLiteral(input, scope, text.Span);
            var markup = MarkupExtensionParser.Parse(input, text.Span, context.Diagnostics.Add);
            if (markup == null || scope.Expand(markup.Name) is not { LocalName: "Type", Namespace: { } ns } || !XamlNames.IsLanguage(ns)) return null;
            return (context.Values.BindText(input, context.Types.Find(ClrNames.Type)!, scope, text.Span) as BoundTypeExpression)?.ReferencedType;
        }
        if (value is XamlElementSyntax element && scope.Push(element).Expand(element.Name) is { LocalName: "Type", Namespace: { } xmlns } && XamlNames.IsLanguage(xmlns))
            return (context.Values.BindNode(element, context.Types.Find(ClrNames.Type)!, scope, nameScope) as BoundTypeExpression)?.ReferencedType;
        return null;
    }
}
