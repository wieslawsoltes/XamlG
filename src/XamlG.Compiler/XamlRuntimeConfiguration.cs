using System.Collections.Immutable;

namespace XamlG.Compiler;

public sealed record XamlRuntimeConfiguration
{
    public XamlTargetPropertyMode TargetPropertyMode { get; init; } = XamlTargetPropertyMode.ReflectionMember;
    public bool ProtectNamespaceDictionaries { get; init; } = true;
    public ImmutableArray<XamlServiceMapping> Services { get; init; } = ImmutableArray<XamlServiceMapping>.Empty;
    public XamlMethodReference? RootServiceProviderFactory { get; init; }
    public XamlMethodReference? InnerServiceProviderFactory { get; init; }
    public XamlMethodReference? DeferredContentCustomizer { get; init; }
    public XamlNameScopeConfiguration? NameScope { get; init; }
    public string? DeferredDefaultTypeArgument { get; init; }
    public ImmutableArray<string> DeferredTypeArgumentAttributeProperties { get; init; } = ImmutableArray.Create("Type");
}
