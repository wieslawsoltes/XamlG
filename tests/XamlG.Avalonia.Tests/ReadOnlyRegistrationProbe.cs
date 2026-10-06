using Avalonia;

namespace XamlG.Avalonia.Tests;

public sealed class ReadOnlyRegistrationProbe : AvaloniaObject
{
    public static readonly DirectProperty<ReadOnlyRegistrationProbe, int> ValueProperty =
        AvaloniaProperty.RegisterDirect<ReadOnlyRegistrationProbe, int>(nameof(Value), owner => owner.Value);
    public int Value => 17;
}
