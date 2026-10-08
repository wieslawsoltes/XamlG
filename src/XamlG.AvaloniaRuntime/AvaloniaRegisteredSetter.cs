using Avalonia;
using Avalonia.Data;
using XamlG.Runtime;

namespace XamlG.AvaloniaRuntime;

/// <summary>Writes through Avalonia's public registered-property API. The registration
/// remains responsible for validation, read-only enforcement, notifications and binding lifetime.</summary>
public static class AvaloniaRegisteredSetter
{
    public static void AssignTemplate<T>(AvaloniaObject target, StyledProperty<T> property, T value) =>
        target.SetValue(property, value, BindingPriority.Template);

    /// <summary>Shares template binding dispatch while preserving template priority and subscription ownership.</summary>
    public static void AssignTemplateValueOrBinding<T>(AvaloniaObject target, object? value,
        StyledProperty<T> property, IServiceProvider services)
    {
        if (value is BindingBase binding)
        {
            var subscription = Assign(target, property, binding);
            ((XamlRuntimeContext)services.GetService(typeof(XamlRuntimeContext))!).Session.TrackDisposable(subscription);
        }
        else AssignTemplate(target, property, (T)value!);
    }

    public static IDisposable? Assign(AvaloniaObject target, AvaloniaProperty property, object? value)
    {
        if (value is BindingBase binding) return AvaloniaBindingAdapter.Apply(target, property, binding);
        AssignValue(target, property, value);
        return null;
    }

    public static void AssignValue(AvaloniaObject target, AvaloniaProperty property, object? value)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        target.SetValue(property, value);
    }

    public static IDisposable? AssignBindingOrUnset(AvaloniaObject target, AvaloniaProperty property, object? value)
    {
        if (value is UnsetValueType)
        {
            AssignValue(target, property, AvaloniaProperty.UnsetValue);
            return null;
        }
        return AssignBinding(target, property, value);
    }

    public static IDisposable AssignBinding(AvaloniaObject target, AvaloniaProperty property, object? value)
    {
        if (value is null) throw new NullReferenceException("No registered-property setter accepts null.");
        return AvaloniaBindingAdapter.Apply(target, property, (BindingBase)value);
    }
}
