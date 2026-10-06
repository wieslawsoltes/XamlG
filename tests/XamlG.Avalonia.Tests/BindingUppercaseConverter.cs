using System.Globalization;
using Avalonia.Data.Converters;

namespace XamlG.Avalonia.Tests;

public sealed class BindingUppercaseConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? text.ToUpperInvariant() : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
