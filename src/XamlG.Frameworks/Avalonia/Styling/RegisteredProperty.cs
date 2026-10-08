using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

internal sealed record RegisteredProperty(ISymbol Declaration, ITypeSymbol FieldType, ITypeSymbol PropertyValueType,
    string? GeneratedMemberName = null) : AvaloniaPropertyReference(PropertyValueType)
{
    public RegisteredProperty(IFieldSymbol field, ITypeSymbol valueType) : this(field, field.Type, valueType) { }
    public string FieldName => GeneratedMemberName ?? Declaration.Name;
    public override BoundExpression Reference(TextSpan span) => new BoundStaticExpression(Declaration, FieldType, span)
        { GeneratedMemberName = GeneratedMemberName };
}
