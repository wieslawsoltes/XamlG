using Avalonia;
using Avalonia.Data;

namespace XamlG.AvaloniaRuntime;

public static class AvaloniaBindingAdapter
{
    public static IDisposable Apply(AvaloniaObject target, AvaloniaProperty property, BindingBase binding)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(binding);
        return target.Bind(property, binding);
    }
}
