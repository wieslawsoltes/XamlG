using System.Collections.Immutable;

namespace XamlG.Compiler;

/// <summary>Describes a runtime service by exact metadata identity and configurable member roles.</summary>
public sealed record XamlServiceMapping(string InterfaceMetadataName, XamlServiceKind Kind, string? NamespaceItemMetadataName = null)
{
    public ImmutableDictionary<string, XamlServiceValue> MemberOverrides { get; init; } =
        ImmutableDictionary<string, XamlServiceValue>.Empty.WithComparers(StringComparer.Ordinal);
    public string NamespaceNameProperty { get; init; } = "ClrNamespace";
    public string AssemblyNameProperty { get; init; } = "ClrAssemblyName";

    public XamlServiceValue? GetValue(string memberName)
    {
        if (MemberOverrides.TryGetValue(memberName, out var value)) return value;
        return (Kind, memberName) switch
        {
            (XamlServiceKind.RootObject, "RootObject") => XamlServiceValue.RootObject,
            (XamlServiceKind.RootObject, "IntermediateRootObject") => XamlServiceValue.IntermediateRootObject,
            (XamlServiceKind.ProvideValueTarget, "TargetObject") => XamlServiceValue.TargetObject,
            (XamlServiceKind.ProvideValueTarget, "TargetProperty") => XamlServiceValue.TargetProperty,
            (XamlServiceKind.ParentStack, "Parents") => XamlServiceValue.Parents,
            (XamlServiceKind.UriContext, "BaseUri") => XamlServiceValue.BaseUri,
            (XamlServiceKind.XmlNamespaces, "XmlNamespaces") => XamlServiceValue.XmlNamespaces,
            _ => null
        };
    }
}
