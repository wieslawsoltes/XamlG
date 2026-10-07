using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Bindings;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

internal static class AvaloniaRegisteredValue
{
    public static IMethodSymbol? Adapter(BindingContext context, string methodName = AvaloniaRegisteredSetterMetadata.Assign) =>
        context.Types.Find(AvaloniaRegisteredSetterMetadata.Adapter)?.Members(methodName)
            .OfType<IMethodSymbol>().SingleOrDefault(method => method.IsStatic && !method.IsGenericMethod && method.Parameters.Length == 3 &&
                method.Parameters[0].Type.HasMetadataName(AvaloniaMetadata.Object) &&
                method.Parameters[1].Type.HasMetadataName(AvaloniaMetadata.Property) &&
                method.Parameters[2].Type.SpecialType == SpecialType.System_Object && context.Types.IsAccessible(method));

    public static BoundExpression? Bind(BindingContext context, ObjectBindingBuilder target, BoundMember member,
        XamlSyntaxNode node, NamespaceScope scope, ITypeSymbol valueType)
    {
        using var expected = new AvaloniaBindingTargetScope(target, member.ValueType);
        if (node is XamlElementSyntax element && new AvaloniaCompiledBindingRule().TryBindElement(context, element, member.ValueType, scope, out var compiled))
            return compiled;
        return context.Values.BindNode(node, valueType, scope, target.NameScopeId, normalizeText: false, member: member.ConversionSource);
    }

    public static BoundExpression? Unset(BindingContext context, TextSpan span)
    {
        var field = context.Types.Find(AvaloniaMetadata.Property)?.GetMembers(AvaloniaRegisteredSetterMetadata.UnsetValue)
            .OfType<IFieldSymbol>().SingleOrDefault(candidate => candidate.IsStatic && context.Types.IsAccessible(candidate));
        if (field != null) return new BoundStaticExpression(field, field.Type, span);
        context.Report("XG3002", "Registered-property assignment requires AvaloniaProperty.UnsetValue.", span);
        return null;
    }
}
