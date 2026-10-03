using Microsoft.CodeAnalysis;
using XamlG.Compiler;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal sealed record BindingAccessor(BoundExpression PropertyInfo, BoundExpression Factory, ITypeSymbol ValueType, bool CanWrite);
