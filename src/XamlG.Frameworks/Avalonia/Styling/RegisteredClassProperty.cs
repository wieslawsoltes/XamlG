using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

internal sealed record RegisteredClassProperty(IMethodSymbol Factory, string Name, ITypeSymbol PropertyValueType)
    : AvaloniaPropertyReference(PropertyValueType)
{
    public override BoundExpression Reference(TextSpan span) => new BoundCallExpression(Factory, null,
        ImmutableArray.Create<BoundExpression>(new BoundConstantExpression(Name, Factory.Parameters[0].Type, span)), span);
}
