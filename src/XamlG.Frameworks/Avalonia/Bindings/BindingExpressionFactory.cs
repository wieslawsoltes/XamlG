using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal sealed class BindingExpressionFactory(BindingContext context)
{
    public BoundExpression Constant(object? value, ITypeSymbol type, TextSpan span) => new BoundConstantExpression(value, type, span);
    public BoundExpression Text(string value, TextSpan span) => Constant(value, context.Types.Special(SpecialType.System_String), span);
    public BoundExpression Number(int value, TextSpan span) => Constant(value, context.Types.Special(SpecialType.System_Int32), span);
    public BoundExpression Type(ITypeSymbol type, TextSpan span) => new BoundTypeExpression(type, context.Types.Find(ClrNames.Type)!, span);

    public BoundExpression? New(string metadataName, TextSpan span, params BoundExpression[] arguments)
    {
        var type = context.Types.Find(metadataName);
        var constructor = type?.InstanceConstructors.FirstOrDefault(m => Matches(m, arguments));
        if (constructor == null) { Missing(metadataName + " constructor", span); return null; }
        return new BoundNewExpression(constructor, arguments.ToImmutableArray(), span);
    }
    public BoundExpression? Call(BoundExpression receiver, string name, TextSpan span, params BoundExpression[] arguments)
    {
        var method = receiver.Type?.Members(name).OfType<IMethodSymbol>().FirstOrDefault(m => !m.IsStatic && !m.IsGenericMethod && Matches(m, arguments));
        if (method == null) { Missing(name, span); return null; }
        return new BoundCallExpression(method, receiver, arguments.ToImmutableArray(), span);
    }
    public BoundExpression? GenericCall(BoundExpression receiver, string name, ITypeSymbol argument, TextSpan span)
    {
        var method = receiver.Type?.Members(name).OfType<IMethodSymbol>().FirstOrDefault(m => !m.IsStatic && m.Arity == 1 && m.Parameters.Length == 0 && context.Types.IsAccessible(m));
        if (method == null) { Missing(name, span); return null; }
        return new BoundCallExpression(method.Construct(argument), receiver, ImmutableArray<BoundExpression>.Empty, span);
    }
    private bool Matches(IMethodSymbol method, IReadOnlyList<BoundExpression> arguments) =>
        method.Parameters.Length == arguments.Count && context.Types.IsAccessible(method) &&
        method.Parameters.Select((p, i) => arguments[i].Type == null ? p.Type.AcceptsNull() : context.Types.Compilation.ClassifyCommonConversion(arguments[i].Type!, p.Type).IsImplicit).All(v => v);
    private void Missing(string name, TextSpan span) => context.Report("XG3202", "The required compiled-binding runtime contract could not be resolved: " + name, span);
}
