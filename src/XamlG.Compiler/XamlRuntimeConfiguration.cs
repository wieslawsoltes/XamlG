using System.Collections.Immutable;
namespace XamlG.Compiler;
public sealed record XamlRuntimeConfiguration
{
    public ImmutableArray<XamlServiceMapping> Services { get; init; } = ImmutableArray<XamlServiceMapping>.Empty;
    public XamlMethodReference? InnerServiceProviderFactory { get; init; }
    public XamlMethodReference? DeferredContentCustomizer { get; init; }
    public string? DeferredDefaultTypeArgument { get; init; }
    public ImmutableArray<string> DeferredTypeArgumentAttributeProperties { get; init; } = ImmutableArray.Create("Type");
}
