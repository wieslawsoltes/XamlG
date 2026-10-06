using Avalonia.Styling;
namespace XamlG.Avalonia.Tests;
public sealed class CountingThemeKeyExtension
{
    public static int Evaluations;
    public ThemeVariant ProvideValue() => ++Evaluations == 1 ? ThemeVariant.Light : ThemeVariant.Dark;
}
