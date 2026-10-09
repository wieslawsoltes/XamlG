using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;
using XamlG.Internal;

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
                var parts = new SpanSplitEnumerator(uri.AsSpan(14), ';');
                parts.MoveNext();
                var ns = parts.Current.ToString();
                string? assembly = null;
                while (parts.MoveNext())
                    if (parts.Current.StartsWith("assembly=".AsSpan(), StringComparison.Ordinal))
                    { assembly = parts.Current.Slice(9).ToString(); break; }
                assembly ??= context.Types.Configuration.DefaultAssemblyName ?? context.Types.Compilation.AssemblyName;
                mappings.Add(new(uri, ns, assembly));
            }
            else if (uri.StartsWith("using:", StringComparison.Ordinal))
            {
                // "using:" records a namespace independent of a particular assembly.
                // Preserve that meaning instead of expanding it into loaded assemblies.
                mappings.Add(new(uri, uri.Substring(6)));
            }
        }
        return mappings.Distinct().ToImmutableArray();
    }
}
