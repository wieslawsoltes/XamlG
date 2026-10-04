using Avalonia.Controls;
using Avalonia.Styling;

namespace XamlG.AvaloniaRuntime;

/// <summary>Direct dictionary operations emitted for statically resolved merge includes. Never loads or parses XAML.</summary>
public static class AvaloniaCompiledResourceOperations
{
    public static void Merge(ResourceDictionary target, ResourceDictionary source)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(target, source)) throw new InvalidOperationException("A dictionary cannot merge itself.");
        foreach (var dictionary in source.MergedDictionaries) target.MergedDictionaries.Add(dictionary);
        foreach (var item in source.ThemeDictionaries) MergeTheme(target, item.Key, item.Value);
        foreach (var item in source) target[item.Key] = item.Value;
    }
    public static void SetResource(ResourceDictionary target, object key, object? value) => target[key] = value;
    public static void MergeTheme(ResourceDictionary target, ThemeVariant key, IThemeVariantProvider value)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(key); ArgumentNullException.ThrowIfNull(value);
        if (!target.ThemeDictionaries.TryGetValue(key, out var previous)) { target.ThemeDictionaries.Add(key, value); return; }
        if (previous is ResourceDictionary oldDictionary && value is ResourceDictionary newDictionary)
        {
            var combined = new ResourceDictionary();
            Merge(combined, oldDictionary); Merge(combined, newDictionary);
            target.ThemeDictionaries[key] = combined;
        }
        else target.ThemeDictionaries[key] = value;
    }
}
