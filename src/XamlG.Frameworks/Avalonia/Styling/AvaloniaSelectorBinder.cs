using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

/// <summary>Lowers selector syntax to public, symbol-resolved API calls rather than runtime parsing.</summary>
internal sealed class AvaloniaSelectorBinder(BindingContext context, NamespaceScope scope, INamedTypeSymbol? nestingType)
{
    public BoundSelector? Bind(SelectorListSyntax syntax)
    {
        var selectorType = context.Types.Find(AvaloniaStyleMetadata.Selector);
        if (selectorType == null) { context.Report("XG3104", "The Avalonia selector contract is unavailable.", syntax.Span); return null; }
        var values = new List<BoundSelector>();
        foreach (var sequence in syntax.Selectors)
        {
            var bound = Sequence(sequence, selectorType);
            if (bound == null) return null;
            values.Add(bound);
        }
        if (values.Count == 1) return values[0];
        var array = context.Types.Compilation.CreateArrayTypeSymbol(selectorType);
        var expression = Call("Or", syntax.Span, new BoundArrayExpression(values.Select(v => v.Expression).ToImmutableArray(), array, syntax.Span));
        INamedTypeSymbol? common = values[0].TargetType;
        foreach (var value in values.Skip(1))
        {
            if (value.TargetType == null) { common = null; break; }
            while (common != null && !context.Types.Compilation.ClassifyCommonConversion(value.TargetType, common).IsImplicit) common = common.BaseType;
        }
        return expression == null ? null : new(expression, common);
    }

    private BoundSelector? Sequence(SelectorSequenceSyntax syntax, INamedTypeSymbol selectorType)
    {
        BoundExpression value = new BoundConstantExpression(null, selectorType, syntax.Span);
        INamedTypeSymbol? target = null;
        foreach (var step in syntax.Steps)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            BoundExpression? result;
            switch (step.Kind)
            {
                case SelectorStepKind.Type:
                case SelectorStepKind.Is:
                case SelectorStepKind.Universal:
                    target = step.Kind == SelectorStepKind.Universal ? context.Types.Find(AvaloniaStyleMetadata.StyledElement) : context.ResolveType(step.Name.Replace('|', ':'), scope, step.Span);
                    if (target == null) return null;
                    var styled = context.Types.Find(AvaloniaStyleMetadata.StyledElement);
                    if (styled == null || !context.Types.Compilation.ClassifyCommonConversion(target, styled).IsImplicit)
                    { context.Report("XG3105", $"Selector type '{target}' must derive from StyledElement.", step.Span); return null; }
                    result = Call(step.Kind == SelectorStepKind.Type ? "OfType" : "Is", step.Span, value,
                        new BoundTypeExpression(target, context.Types.Find(ClrNames.Type)!, step.Span));
                    break;
                case SelectorStepKind.Class:
                case SelectorStepKind.Name:
                    result = Call(step.Kind == SelectorStepKind.Class ? "Class" : "Name", step.Span, value, Text(step.Name, step.Span));
                    break;
                case SelectorStepKind.Child:
                case SelectorStepKind.Descendant:
                case SelectorStepKind.Template:
                    result = Call(step.Kind.ToString(), step.Span, value);
                    target = null;
                    break;
                case SelectorStepKind.Nesting:
                    if (nestingType == null) { context.Report("XG3106", "A nesting selector requires an enclosing style or control theme target.", step.Span); return null; }
                    target = nestingType;
                    result = Call("Nesting", step.Span, value);
                    break;
                case SelectorStepKind.PropertyEquals:
                    var property = AvaloniaRegisteredPropertyResolver.Resolve(context, target, step.Name, scope, step.Span);
                    if (property == null) return null;
                    var converted = context.Values.BindText(step.Value!, property.ValueType, scope, step.Span);
                    if (converted == null) return null;
                    result = Call("PropertyEquals", step.Span, value, property.Reference(step.Span), converted);
                    break;
                case SelectorStepKind.Not:
                    var argument = Bind(step.Argument!);
                    if (argument == null) return null;
                    result = Call("Not", step.Span, value, argument.Expression);
                    break;
                case SelectorStepKind.NthChild:
                case SelectorStepKind.NthLastChild:
                    result = Call(step.Kind.ToString(), step.Span, value,
                        new BoundConstantExpression(step.Step, context.Types.Special(SpecialType.System_Int32), step.Span),
                        new BoundConstantExpression(step.Offset, context.Types.Special(SpecialType.System_Int32), step.Span));
                    break;
                default: throw new InvalidOperationException("Unknown selector syntax kind.");
            }
            if (result == null) return null;
            value = result;
        }
        return new(value, target);
    }

    private BoundExpression Text(string value, TextSpan span) => new BoundConstantExpression(value, context.Types.Special(SpecialType.System_String), span);
    private BoundExpression? Call(string name, TextSpan span, params BoundExpression[] arguments)
    {
        var method = context.Types.Find(AvaloniaStyleMetadata.Selectors)?.GetMembers(name).OfType<IMethodSymbol>()
            .Where(m => m.IsStatic && !m.IsGenericMethod && m.Parameters.Length == arguments.Length && context.Types.IsAccessible(m))
            .Where(m => m.Parameters.Select((p, i) => arguments[i].Type == null ? p.Type.AcceptsNull() : context.Types.Compilation.ClassifyCommonConversion(arguments[i].Type!, p.Type).IsImplicit).All(v => v))
            .OrderByDescending(m => m.Parameters.Count(p => p.Type is IArrayTypeSymbol)).FirstOrDefault();
        if (method == null) { context.Report("XG3104", "Required Avalonia selector overload is missing: " + name, span); return null; }
        return new BoundCallExpression(method, null, arguments.ToImmutableArray(), span);
    }
}
