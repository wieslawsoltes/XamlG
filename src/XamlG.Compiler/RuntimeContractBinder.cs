using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
namespace XamlG.Compiler;

internal static class RuntimeContractBinder
{
    public static BoundRuntimeConfiguration Bind(BindingContext context)
    {
        var services = ImmutableArray.CreateBuilder<BoundServiceContract>();
        foreach (var mapping in context.Profile.Runtime.Services)
        {
            var type = context.Types.Find(mapping.InterfaceMetadataName);
            if (type == null || type.TypeKind != TypeKind.Interface || !context.Types.IsAccessible(type))
            { context.Report("XG1100", $"Service contract '{mapping.InterfaceMetadataName}' must resolve to an accessible interface.", new(0, 0)); continue; }
            services.Add(new(mapping, type, mapping.NamespaceItemMetadataName == null ? null : context.Types.Find(mapping.NamespaceItemMetadataName)));
        }
        IMethodSymbol? Resolve(XamlMethodReference? reference, int parameters)
        {
            if (reference == null) return null;
            var methods = context.Types.Find(reference.TypeMetadataName)?.Members(reference.MethodName).OfType<IMethodSymbol>().Where(m => m.IsStatic && m.Parameters.Length == parameters && context.Types.IsAccessible(m)).ToArray() ?? Array.Empty<IMethodSymbol>();
            if (methods.Length == 1) return methods[0];
            context.Report("XG1101", $"Runtime method '{reference.TypeMetadataName}.{reference.MethodName}' is missing or ambiguous.", new(0, 0)); return null;
        }
        return new(services.ToImmutable(), Resolve(context.Profile.Runtime.InnerServiceProviderFactory, 1), Resolve(context.Profile.Runtime.DeferredContentCustomizer, 2), context.Types.NamespaceMappings, context.Types.Compilation.AssemblyName ?? string.Empty);
    }
}
