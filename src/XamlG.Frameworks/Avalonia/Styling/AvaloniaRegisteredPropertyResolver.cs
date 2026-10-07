using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

internal static class AvaloniaRegisteredPropertyResolver
{
    public static RegisteredProperty? Resolve(BindingContext context, ITypeSymbol? targetType, string text,
        NamespaceScope scope, TextSpan span, bool report = true)
    {
        text = text.Trim();
        if (text.StartsWith("(", StringComparison.Ordinal) && text.EndsWith(")", StringComparison.Ordinal)) text = text.Substring(1, text.Length - 2).Trim();
        var separator = text.LastIndexOf('.');
        var name = separator < 0 ? text : text.Substring(separator + 1);
        var owner = separator < 0 ? targetType : context.ResolveType(text.Substring(0, separator).Replace('|', ':'), scope, span, report: report);
        if (owner == null)
        {
            if (report) context.Report("XG3102", "A property reference requires a target type or an explicit owner type.", span);
            return null;
        }
        if (Find(context, owner, name) is { } property)
        {
            context.Symbols.Add(new(span, property.Field, "registered-property"));
            return property;
        }
        if (report) context.Report("XG3103", $"Registered property '{text}' was not found on '{owner.ToDisplayString()}'.", span);
        return null;
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
