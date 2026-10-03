using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

internal sealed record RegisteredProperty(IFieldSymbol Field, ITypeSymbol ValueType)
{
    public BoundExpression Reference(TextSpan span) => new BoundStaticExpression(Field, Field.Type, span);
}
