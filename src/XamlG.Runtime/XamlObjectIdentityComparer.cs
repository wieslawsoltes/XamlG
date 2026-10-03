using System.Runtime.CompilerServices;

namespace XamlG.Runtime;

internal sealed class XamlObjectIdentityComparer : IEqualityComparer<object>
{
    public static XamlObjectIdentityComparer Instance { get; } = new();
    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
    public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
}
