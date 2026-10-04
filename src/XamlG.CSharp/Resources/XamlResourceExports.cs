using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;

namespace XamlG.CSharp.Resources;

internal static class XamlResourceExports
{
    public static XamlEmissionResult Add(BoundDocument document, XamlEmissionResult output)
    {
        if (!output.Success || document.Options.ResourceUri == null || output.BuildMethodName == null || !IsPublic(document.Root!.Type)) return output;
        var prefix = "[assembly: global::XamlG.Runtime.XamlCompiledResourceAttribute(" + CSharpNames.Literal(document.Options.ResourceUri) +
            ", typeof(global::" + output.FactoryTypeName + "), " + CSharpNames.Literal(output.BuildMethodName) + ")]\n";
        return output with
        {
            Source = prefix + output.Source,
            SourceMappings = output.SourceMappings.Select(m => m with { GeneratedSpan = new TextSpan(m.GeneratedSpan.Start + prefix.Length, m.GeneratedSpan.Length) }).ToImmutableArray()
        };
    }
    private static bool IsPublic(INamedTypeSymbol type)
    {
        for (var current = type; current != null; current = current.ContainingType)
            if (current.DeclaredAccessibility != Accessibility.Public) return false;
        return type.TypeArguments.OfType<INamedTypeSymbol>().All(t => SymbolEqualityComparer.Default.Equals(t, type) || IsPublic(t));
    }
}
