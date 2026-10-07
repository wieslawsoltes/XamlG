using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Supplies Avalonia's synthetic type-converter attributes as Roslyn symbols.</summary>
public sealed class AvaloniaTypeConverterProvider : IXamlTypeConverterProvider
{
    public INamedTypeSymbol? GetConverter(BindingContext context, ITypeSymbol targetType)
    {
        if (targetType is not INamedTypeSymbol type) return null;
        if (type.OriginalDefinition.HasMetadataName(AvaloniaRegisteredSetterMetadata.List))
            return context.Types.Find(AvaloniaConverterMetadata.ListConverter)?.Construct(type.TypeArguments.ToArray());
        var converter = type.MetadataName() switch
        {
            AvaloniaConverterMetadata.Image or AvaloniaConverterMetadata.Bitmap or AvaloniaConverterMetadata.ImageBrushSource => AvaloniaConverterMetadata.BitmapConverter,
            AvaloniaConverterMetadata.WindowIcon => AvaloniaConverterMetadata.IconConverter,
            ClrNames.CultureInfo => AvaloniaConverterMetadata.CultureConverter,
            ClrNames.Uri => AvaloniaConverterMetadata.UriConverter,
            ClrNames.TimeSpan => AvaloniaConverterMetadata.TimeSpanConverter,
            AvaloniaLiteralMetadata.FontFamily => AvaloniaConverterMetadata.FontFamilyConverter,
            _ => null
        };
        if (type.OriginalDefinition.HasMetadataName(AvaloniaConverterMetadata.GenericList) && type.TypeArguments[0].HasMetadataName(AvaloniaConverterMetadata.Point))
            converter = AvaloniaConverterMetadata.PointsConverter;
        return converter == null ? null : context.Types.Find(converter);
    }
}
