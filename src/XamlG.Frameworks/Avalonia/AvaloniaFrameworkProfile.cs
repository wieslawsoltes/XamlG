using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.Frameworks.Avalonia;

public static class AvaloniaFrameworkProfile
{
    public static XamlFrameworkProfile Create() => XamlFrameworkProfile.Portable with
    {
        Name = "Avalonia",
        TypeSystem = new XamlTypeSystemConfiguration
        {
            XmlnsDefinitionAttributes = ImmutableArray.Create(AvaloniaMetadata.XmlnsDefinition),
            ContentAttributes = ImmutableArray.Create(AvaloniaMetadata.Content),
            DeferredContentAttributes = ImmutableArray.Create(AvaloniaMetadata.DeferredContent),
            WhitespaceSignificantCollectionAttributes = ImmutableArray.Create(AvaloniaMetadata.WhitespaceSignificant),
            TrimSurroundingWhitespaceAttributes = ImmutableArray.Create(AvaloniaMetadata.TrimSurroundingWhitespace),
            UsableDuringInitializationAttributes = ImmutableArray.Create(AvaloniaMetadata.UsableDuringInitialization),
            IgnoredNamespaces = ImmutableHashSet.Create(StringComparer.Ordinal, AvaloniaMetadata.DesignNamespace)
        }
    };
}
