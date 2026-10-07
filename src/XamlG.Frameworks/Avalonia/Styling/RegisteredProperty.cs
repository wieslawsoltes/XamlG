using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

internal sealed record RegisteredProperty(IFieldSymbol Field, ITypeSymbol PropertyValueType) : AvaloniaPropertyReference(PropertyValueType)
{
    public override BoundExpression Reference(TextSpan span) => new BoundStaticExpression(Field, Field.Type, span);
}
