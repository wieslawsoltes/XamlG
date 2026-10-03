using Avalonia;
using Avalonia.Data;

namespace XamlG.AvaloniaRuntime;

public static class AvaloniaBindingAdapter
{
    public static void Apply(AvaloniaObject target, AvaloniaProperty property, BindingBase binding)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(binding);
        target.Bind(property, binding);
    }
}
