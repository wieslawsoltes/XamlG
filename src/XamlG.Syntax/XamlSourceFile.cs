namespace XamlG.Syntax;

/// <summary>Shared host-level XAML extension policy; extensions do not select a framework.</summary>
public static class XamlSourceFile
{
    public static bool IsSupported(string path) => path != null &&
        (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".paml", StringComparison.OrdinalIgnoreCase));
}
