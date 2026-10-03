using System.Collections.Immutable;
namespace XamlG.Roslyn;

/// <summary>Immutable framework policy; values are exact metadata names, never simple-name matches.</summary>
public sealed record XamlTypeSystemConfiguration
{
    public string? DefaultAssemblyName { get; init; }
    public bool AllowImplicitNumericConversions { get; init; }
    public ImmutableArray<XamlCollectionProjection> CollectionProjections { get; init; } = ImmutableArray.Create(
        new XamlCollectionProjection(ClrNames.IEnumerable, ClrNames.IList),
        new XamlCollectionProjection(ClrNames.IEnumerableOfT, ClrNames.ICollectionOfT));
    public ImmutableArray<XmlNamespaceMapping> NamespaceMappings { get; init; } = ImmutableArray<XmlNamespaceMapping>.Empty;
    public ImmutableArray<string> XmlnsDefinitionAttributes { get; init; } = ImmutableArray.Create("XamlG.Runtime.XmlnsDefinitionAttribute");
    public ImmutableArray<string> ContentAttributes { get; init; } = ImmutableArray.Create("XamlG.Runtime.ContentAttribute");
    public ImmutableArray<string> DeferredContentAttributes { get; init; } = ImmutableArray.Create("XamlG.Runtime.DeferredContentAttribute");
    public ImmutableArray<string> WhitespaceSignificantCollectionAttributes { get; init; } = ImmutableArray.Create("XamlG.Runtime.WhitespaceSignificantCollectionAttribute");
    public ImmutableArray<string> TrimSurroundingWhitespaceAttributes { get; init; } = ImmutableArray.Create("XamlG.Runtime.TrimSurroundingWhitespaceAttribute");
    public ImmutableArray<string> UsableDuringInitializationAttributes { get; init; } = ImmutableArray.Create("XamlG.Runtime.UsableDuringInitializationAttribute");
    public ImmutableArray<string> TypeConverterAttributes { get; init; } = ImmutableArray.Create(ClrNames.TypeConverterAttribute);
    public ImmutableArray<string> AddChildInterfaces { get; init; } = ImmutableArray<string>.Empty;
    public ImmutableHashSet<string> IgnoredNamespaces { get; init; } = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    public string MarkupExtensionSuffix { get; init; } = "Extension";
    public string MarkupExtensionMethod { get; init; } = "ProvideValue";
    public string CollectionAddMethod { get; init; } = ClrNames.Add;
    public string AddChildMethod { get; init; } = "AddChild";
    public string ContentPropertyAttributeProperty { get; init; } = "Name";
}
