using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

internal static class AvaloniaRegisteredPropertyResolver
{
    public static AvaloniaPropertyReference? Resolve(BindingContext context, ITypeSymbol? targetType, string text,
        NamespaceScope scope, TextSpan span, bool report = true)
    {
        var parsed = AvaloniaPropertyName.Parse(text);
        if (parsed == null)
        {
            if (report) context.Report("XG3103", "Invalid registered-property name '" + text + "'.", span);
            return null;
        }
        var name = parsed.Owner == null ? text : parsed.Name;
        if (parsed.Prefix == null && string.Equals(parsed.Owner, "Classes", StringComparison.OrdinalIgnoreCase))
        {
            var factory = context.Types.Find(AvaloniaStyleMetadata.StyledElementExtensions)?.GetMembers(AvaloniaStyleMetadata.ClassPropertyFactory)
                .OfType<IMethodSymbol>().FirstOrDefault(method => method.IsStatic && method.Parameters.Length == 1 &&
                    method.Parameters[0].Type.SpecialType == SpecialType.System_String && context.Types.IsAccessible(method));
            if (factory != null) return new RegisteredClassProperty(factory, name, context.Types.Special(SpecialType.System_Boolean));
            if (report) context.Report("XG3104", "The public class-property factory is unavailable.", span);
            return null;
        }
        var owner = parsed.Owner == null ? targetType : context.ResolveType(
            parsed.Prefix == null ? parsed.Owner : parsed.Prefix + ":" + parsed.Owner, scope, span, report: report);
        if (owner == null)
        {
            if (report) context.Report("XG3102", "A property reference requires a target type or an explicit owner type.", span);
            return null;
        }
        var property = parsed.Owner == null ? FindUnqualified(context, owner, name) : Find(context, owner, name);
        if (property != null)
        {
            context.Symbols.Add(new(span, property.Field, "registered-property"));
            return property;
        }
        if (report) context.Report("XG3103", $"Registered property '{text}' was not found on '{owner.ToDisplayString()}'.", span);
        return null;
    }

    private static RegisteredProperty? FindUnqualified(BindingContext context, ITypeSymbol owner, string name)
    {
        var wrapper = owner.Members(name).OfType<IPropertySymbol>().FirstOrDefault(candidate => !candidate.IsStatic && !candidate.IsIndexer && context.Types.IsAccessible(candidate));
        var @event = wrapper == null ? owner.Members(name).OfType<IEventSymbol>().FirstOrDefault(candidate =>
            !candidate.IsStatic && candidate.AddMethod != null && context.Types.IsAccessible(candidate.AddMethod)) : null;
        var declaringType = wrapper?.ContainingType ?? @event?.ContainingType ?? owner;
        var valueType = wrapper?.Type ?? @event?.Type;
        if (valueType == null)
        {
            IMethodSymbol? getter = null, setter = null, adder = null;
            foreach (var method in owner.GetMembers().OfType<IMethodSymbol>())
            {
                if (!method.IsStatic || method.DeclaredAccessibility != Accessibility.Public || method.Parameters.Length == 0 ||
                    !context.Types.Compilation.ClassifyCommonConversion(owner, method.Parameters[0].Type).IsImplicit) continue;
                if (method.Name == "Get" + name && method.Parameters.Length == 1) getter = method;
                if (method.Name == "Set" + name && method.Parameters.Length == 2) setter = method;
                if (method.Name == "Add" + name + "Handler" && method.Parameters.Length == 2) adder = method;
            }
            valueType = getter?.ReturnType ?? setter?.Parameters[1].Type ?? adder?.Parameters[1].Type;
        }
        var field = declaringType.GetMembers(name + AvaloniaMetadata.PropertySuffix).OfType<IFieldSymbol>()
            .FirstOrDefault(candidate => candidate.IsStatic && context.Types.IsAccessible(candidate));
        var propertyType = context.Types.Find(AvaloniaStyleMetadata.Property);
        return valueType != null && field != null && propertyType != null &&
            context.Types.Compilation.ClassifyCommonConversion(field.Type, propertyType).IsImplicit ? new(field, valueType) : null;
    }

    public static RegisteredProperty? Find(BindingContext context, ITypeSymbol owner, string name)
    {
        var field = owner.Members(name + AvaloniaMetadata.PropertySuffix).OfType<IFieldSymbol>()
            .FirstOrDefault(f => f.IsStatic && context.Types.IsAccessible(f));
        if (field != null)
            for (var current = field.Type as INamedTypeSymbol; current != null; current = current.BaseType)
                if (current.OriginalDefinition.HasMetadataName(AvaloniaStyleMetadata.GenericProperty))
                    return new(field, current.TypeArguments[0]);
        return null;
    }
}
