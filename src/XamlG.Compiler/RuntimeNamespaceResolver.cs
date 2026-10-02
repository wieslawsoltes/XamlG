using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Compiler;

internal static class RuntimeNamespaceResolver
{
    public static ImmutableArray<XmlNamespaceMapping> Collect(BindingContext context)
    {
        var mappings = context.Types.NamespaceMappings.ToBuilder();
        var assemblies = ImmutableArray.Create(context.Types.Compilation.Assembly)
            .AddRange(context.Types.Compilation.SourceModule.ReferencedAssemblySymbols);
        var declarations = context.Syntax.Root?.DescendantsAndSelf()
            .SelectMany(e => e.Attributes).Where(a => a.IsNamespace).Select(a => a.Value).Distinct(StringComparer.Ordinal)
            ?? Enumerable.Empty<string>();

        foreach (var uri in declarations)
        {
            if (XamlNames.IsLanguage(uri))
            {
                mappings.Add(new(uri, "System", context.Types.Special(SpecialType.System_Object).ContainingAssembly.Identity.Name));
            }
            else if (uri.StartsWith("clr-namespace:", StringComparison.Ordinal))
            {
                var parts = uri.Substring(14).Split(';');
                var assembly = parts.Skip(1).FirstOrDefault(p => p.StartsWith("assembly=", StringComparison.Ordinal))?.Substring(9)
                    ?? context.Types.Compilation.AssemblyName;
                mappings.Add(new(uri, parts[0], assembly));
            }
            else if (uri.StartsWith("using:", StringComparison.Ordinal))
            {
                var clrNamespace = uri.Substring(6);
                foreach (var assembly in assemblies)
                {
                    INamespaceSymbol? scope = assembly.GlobalNamespace;
                    foreach (var part in clrNamespace.Split('.'))
                        if (part.Length != 0) scope = scope?.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
                    if (scope != null) mappings.Add(new(uri, clrNamespace, assembly.Identity.Name));
                }
            }
        }
        return mappings.Distinct().ToImmutableArray();
    }
}
