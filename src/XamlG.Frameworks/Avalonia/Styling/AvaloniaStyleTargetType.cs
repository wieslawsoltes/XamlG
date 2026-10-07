using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

internal static class AvaloniaStyleTargetType
{
    public static bool TryRead(BindingContext context, ObjectBindingBuilder target, out INamedTypeSymbol? type)
    {
        type = null;
        var attribute = target.Syntax.Attributes.FirstOrDefault(a => a.Name == AvaloniaStyleMetadata.TargetTypeMember || a.LocalName.EndsWith("." + AvaloniaStyleMetadata.TargetTypeMember, StringComparison.Ordinal));
        var property = target.Syntax.Children.OfType<XamlElementSyntax>().FirstOrDefault(e => e.LocalName.EndsWith("." + AvaloniaStyleMetadata.TargetTypeMember, StringComparison.Ordinal));
        if (attribute == null && property == null) return false;
        var typeType = context.Types.Find(ClrNames.Type)!;
        BoundExpression? value = null;
        if (attribute != null)
            value = context.Values.BindText(attribute.Value, typeType, target.Scope, attribute.ValueSpan);
        else
        {
            var scope = target.Scope.Push(property!);
            var nodes = property!.Children.Where(node => node is XamlElementSyntax || node is XamlTextSyntax text && !string.IsNullOrWhiteSpace(text.Value)).ToArray();
            if (nodes.Length == 1) value = context.Values.BindNode(nodes[0], typeType, scope, target.NameScopeId);
        }
        type = (value as BoundTypeExpression)?.ReferencedType as INamedTypeSymbol;
        if (type == null) context.Report("XG3116", "TargetType requires a statically resolved type.", attribute?.ValueSpan ?? property!.Span);
        return true;
    }
}
