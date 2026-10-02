namespace XamlG.Frameworks.Avalonia;

/// <summary>One versioned metadata contract for the Avalonia adapter. No framework assembly is loaded by the compiler.</summary>
public static class AvaloniaMetadata
{
    public const string Object = "Avalonia.AvaloniaObject";
    public const string XmlnsDefinition = "Avalonia.Metadata.XmlnsDefinitionAttribute";
    public const string Content = "Avalonia.Metadata.ContentAttribute";
    public const string DeferredContent = "Avalonia.Metadata.TemplateContentAttribute";
    public const string WhitespaceSignificant = "Avalonia.Metadata.WhitespaceSignificantCollectionAttribute";
    public const string TrimSurroundingWhitespace = "Avalonia.Metadata.TrimSurroundingWhitespaceAttribute";
    public const string UsableDuringInitialization = "Avalonia.Metadata.UsableDuringInitializationAttribute";
    public const string Loader = "Avalonia.Markup.Xaml.AvaloniaXamlLoader";
    public const string Load = "Load";
    public const string DesignNamespace = "http://schemas.microsoft.com/expression/blend/2008";
}
