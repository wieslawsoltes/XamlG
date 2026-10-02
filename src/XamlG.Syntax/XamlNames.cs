namespace XamlG.Syntax;

/// <summary>Names defined by the XML/XAML languages, not framework-specific type names.</summary>
public static class XamlNames
{
    public const string Language2006 = "http://schemas.microsoft.com/winfx/2006/xaml";
    public const string Language2009 = "http://schemas.microsoft.com/winfx/2009/xaml";
    public const string Xml = "http://www.w3.org/XML/1998/namespace";
    public const string Xmlns = "http://www.w3.org/2000/xmlns/";
    public const string Compatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    public static bool IsLanguage(string value) => value == Language2006 || value == Language2009;
}
