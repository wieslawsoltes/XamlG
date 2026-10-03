using Microsoft.CodeAnalysis;
using XamlG.Compiler;

namespace XamlG.Frameworks.Avalonia.Bindings;

/// <summary>Preserves the eventual target type while a binding object is bound as System.Object.</summary>
internal sealed class AvaloniaBindingTargetScope : IDisposable
{
    private static readonly XamlAnnotationKey<ITypeSymbol?> Key = new("Avalonia.BindingTargetType");
    private readonly ObjectBindingBuilder _target;
    private readonly ITypeSymbol? _previous;

    public AvaloniaBindingTargetScope(ObjectBindingBuilder target, ITypeSymbol type)
    {
        _target = target;
        target.Annotations.TryGet(Key, out _previous);
        target.Annotations.Set(Key, type);
    }

    public static ITypeSymbol? Get(ObjectBindingBuilder target) =>
        target.Annotations.TryGet(Key, out var type) ? type : null;

    public void Dispose() => _target.Annotations.Set(Key, _previous);
}
