using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
namespace XamlG.Compiler;
public sealed record BoundRuntimeConfiguration(
    ImmutableArray<BoundServiceContract> Services,
    IMethodSymbol? InnerServiceProviderFactory,
    IMethodSymbol? DeferredContentCustomizer,
    ImmutableArray<XmlNamespaceMapping> NamespaceMappings,
    string DefaultAssemblyName)
{
    public BoundNameScopeIntegration? NameScope { get; init; }
    public static BoundRuntimeConfiguration Empty { get; } = new(ImmutableArray<BoundServiceContract>.Empty, null, null, ImmutableArray<XmlNamespaceMapping>.Empty, "");
}
