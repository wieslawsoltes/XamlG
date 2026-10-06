namespace XamlG.Avalonia.Tests;
public sealed class ThrowingOptionExtension
{
    public static int Constructions;
    public ThrowingOptionExtension() { Constructions++; throw new InvalidOperationException("An unselected branch was constructed."); }
    public object ProvideValue() => throw new InvalidOperationException();
}
