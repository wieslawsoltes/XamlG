using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Bindings;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.References;

internal static class AvaloniaLiteralName
{
    public static IEnumerable<(string Value, TextSpan Span)> Read(BindingContext context, ObjectBindingBuilder target)
    {
        if (!target.Type.AllInterfaces.Any(type => type.HasMetadataName(AvaloniaMetadata.Named))) yield break;
        foreach (var property in BindingPropertyValue.Read(context, target))
        {
            if (property.MemberName != "Name" || property.Name.Contains(':') && property.Name.IndexOf('.') < 0) continue;
            var member = context.Members.Resolve(target.Type, property.Name, property.Scope, property.NameSpan, report: false);
            if (member?.Symbol is not IPropertySymbol symbol || !symbol.ContainingType.AllInterfaces.Any(type => type.HasMetadataName(AvaloniaMetadata.Named))) continue;
            var elementProperty = target.Syntax.Children.OfType<XamlElementSyntax>().Any(element => element.Span == property.Span);
            var nodes = property.Values.Where(node => node is XamlElementSyntax || node is XamlTextSyntax text && (!elementProperty || !string.IsNullOrWhiteSpace(text.Value))).ToArray();
            if (nodes.Length != 1) continue;
            if (nodes[0] is XamlElementSyntax element && !element.Children.OfType<XamlElementSyntax>().Any() &&
                context.Values.TryGetStringLiteral(element, property.Scope, out var literal))
            {
                var conversion = context.Types.Compilation.ClassifyCommonConversion(context.Types.Special(SpecialType.System_String), member.ValueType);
                if (!conversion.IsImplicit || conversion.IsUserDefined) continue;
                if (literal.Length > 0)
                {
                    literal = XmlWhitespace.Normalize(literal, property.Scope.Push(element).PreserveSpace);
                    if (literal.Length == 0) continue;
                }
                yield return (literal, element.Span); continue;
            }
            if (nodes[0] is not XamlTextSyntax value) continue;
            var name = value.Value;
            if (name.StartsWith("{}", StringComparison.Ordinal)) name = name.Substring(2);
            else if (name.StartsWith("{", StringComparison.Ordinal)) continue;
            if (elementProperty)
                name = XmlWhitespace.Normalize(name, property.Scope.PreserveSpace);
            if (context.Values.TryText(name, member.ValueType, property.Scope, value.Span, member.ConversionSource) is BoundConstantExpression { Value: string registered })
                yield return (registered, value.Span);
        }
    }
}
