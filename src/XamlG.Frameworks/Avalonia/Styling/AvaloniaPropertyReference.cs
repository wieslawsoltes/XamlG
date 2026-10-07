using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

internal abstract record AvaloniaPropertyReference(ITypeSymbol ValueType)
{
    public abstract BoundExpression Reference(TextSpan span);
}
