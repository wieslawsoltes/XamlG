namespace XamlG.Frameworks.Avalonia;

internal static class AvaloniaConverterMetadata
{
    private const string Converters = "Avalonia.Markup.Xaml.Converters.";
    public const string Image = "Avalonia.Media.IImage";
    public const string Bitmap = "Avalonia.Media.Imaging.Bitmap";
    public const string ImageBrushSource = "Avalonia.Media.IImageBrushSource";
    public const string WindowIcon = "Avalonia.Controls.WindowIcon";
    public const string Point = "Avalonia.Point";
    public const string GenericList = "System.Collections.Generic.IList`1";
    public const string BitmapConverter = Converters + "BitmapTypeConverter";
    public const string IconConverter = Converters + "IconTypeConverter";
    public const string PointsConverter = Converters + "PointsListTypeConverter";
    public const string UriConverter = Converters + "AvaloniaUriTypeConverter";
    public const string TimeSpanConverter = Converters + "TimeSpanTypeConverter";
    public const string FontFamilyConverter = Converters + "FontFamilyTypeConverter";
    public const string CultureConverter = "System.ComponentModel.CultureInfoConverter";
    public const string ListConverter = "Avalonia.Collections.AvaloniaListConverter`1";
}
