using Avalonia;
using Avalonia.Data;

namespace XamlG.AvaloniaRuntime;

public static class AvaloniaClassAdapter
{
    public static IDisposable? Apply(StyledElement target, string className, object? value)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        if (value is BindingBase binding) return target.BindClass(className, binding, target);
        if (value is bool enabled) { target.Classes.Set(className, enabled); return null; }
        throw new InvalidCastException("A conditional class requires a Boolean or a binding value.");
    }
}
