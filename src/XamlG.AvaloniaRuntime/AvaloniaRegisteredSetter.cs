using Avalonia;
using Avalonia.Data;

namespace XamlG.AvaloniaRuntime;

/// <summary>Writes through Avalonia's public registered-property API. The registration
/// remains responsible for validation, read-only enforcement, notifications and binding lifetime.</summary>
public static class AvaloniaRegisteredSetter
{
    public static IDisposable? Assign(AvaloniaObject target, AvaloniaProperty property, object? value)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        if (value is BindingBase binding) return AvaloniaBindingAdapter.Apply(target, property, binding);
        target.SetValue(property, value);
        return null;
    }
}
