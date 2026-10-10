using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;

namespace XamlG.CSharp.Resources;

internal static class XamlResourceExports
{
    public static void Emit(EmissionContext context, string factoryTypeName, string? buildMethodName)
    {
        var document = context.Document;
        if (context.Diagnostics.Any(diagnostic => diagnostic.Severity == XamlSeverity.Error) || document.ClassModifier != "public" ||
            document.Options.ResourceUri == null || buildMethodName == null || !IsPublic(document.Root!.Type)) return;
        var prefix = "[assembly: global::XamlG.Runtime.XamlCompiledResourceAttribute(" + CSharpNames.Literal(document.Options.ResourceUri) +
            ", typeof(global::" + factoryTypeName + "), " + CSharpNames.Literal(buildMethodName) + ")]\n";
        // Add exports before materializing the generated string. Prefixing the
        // final string copied the entire document, including all nested templates.
        context.Writer.Prepend(prefix);
        for (var index = 0; index < context.Mappings.Count; index++)
        {
            var mapping = context.Mappings[index];
            context.Mappings[index] = mapping with { GeneratedSpan = new TextSpan(mapping.GeneratedSpan.Start + prefix.Length, mapping.GeneratedSpan.Length) };
        }
    }
    private static bool IsPublic(INamedTypeSymbol type)
    {
        for (var current = type; current != null; current = current.ContainingType)
            if (current.DeclaredAccessibility != Accessibility.Public) return false;
        return type.TypeArguments.OfType<INamedTypeSymbol>().All(t => SymbolEqualityComparer.Default.Equals(t, type) || IsPublic(t));
    }
}
