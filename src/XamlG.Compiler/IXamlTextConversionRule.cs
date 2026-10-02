using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;
namespace XamlG.Compiler;
/// <summary>Compile-time conversion supplied by a framework; implementations must not execute user code.</summary>
public interface IXamlTextConversionRule
{
    bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope, TextSpan span,
        ISymbol? member, out BoundExpression? expression);
}
