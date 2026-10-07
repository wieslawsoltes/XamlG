using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

internal sealed class AvaloniaContainerQueryBinder(BindingContext context)
{
    public BoundExpression? Bind(ContainerQuerySyntax syntax)
    {
        var query = context.Types.Find(AvaloniaStyleMetadata.StyleQuery);
        var comparison = context.Types.Find(AvaloniaStyleMetadata.QueryComparison);
        if (query == null || comparison == null) return Missing("StyleQuery", syntax.Span);
        BoundExpression initial = new BoundConstantExpression(null, query, syntax.Span);
        var current = initial;
        List<BoundExpression>? alternatives = null;
        List<BoundExpression>? conjunction = null;
        foreach (var step in syntax.Steps)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (step.Kind == ContainerQueryStepKind.Or)
            {
                alternatives ??= new();
                if (conjunction != null && ReferenceEquals(current, initial))
                { context.Report("XG3111", "An 'and' query requires a following feature.", step.Span); return null; }
                var group = conjunction == null ? current : Combine("And", conjunction);
                if (group == null) return null;
                alternatives.Add(group);
                current = initial;
                conjunction = null;
            }
            else if (step.Kind == ContainerQueryStepKind.And)
            {
                conjunction ??= new();
                conjunction.Add(current);
                current = initial;
            }
            else
            {
                var field = comparison.GetMembers(step.Comparison).OfType<IFieldSymbol>().FirstOrDefault();
                if (field == null) return Missing(step.Comparison, step.Span);
                var result = Call(step.Kind.ToString(), step.Span, current, new BoundEnumExpression(ImmutableArray.Create(field), comparison, step.Span),
                    new BoundConstantExpression(step.Value, context.Types.Special(SpecialType.System_Double), step.Span));
                if (result == null) return null;
                current = result;
            }
            if (conjunction != null && !ReferenceEquals(current, initial)) conjunction.Add(current);
        }
        // The pinned compiler closes conjunctions at commas and retains the final
        // feature for the last branch. Preserve that behavior, including query text.
        if (alternatives == null) return current;
        alternatives.Add(current);
        return Combine("Or", alternatives);

        BoundExpression? Combine(string name, List<BoundExpression> values) => values.Count == 1 ? values[0] :
            Call(name, syntax.Span, new BoundArrayExpression(values.ToImmutableArray(), context.Types.Compilation.CreateArrayTypeSymbol(query), syntax.Span));
    }

    private BoundExpression? Call(string name, TextSpan span, params BoundExpression[] arguments)
    {
        var method = context.Types.Find(AvaloniaStyleMetadata.StyleQueries)?.GetMembers(name).OfType<IMethodSymbol>()
            .Where(method => method.IsStatic && !method.IsGenericMethod && context.Types.IsAccessible(method) && method.Parameters.Length == arguments.Length)
            .FirstOrDefault(method => method.Parameters.Select((parameter, index) =>
                context.Types.Compilation.ClassifyCommonConversion(arguments[index].Type!, parameter.Type).IsImplicit).All(value => value));
        return method == null ? Missing(name, span) : new BoundCallExpression(method, null, arguments.ToImmutableArray(), span);
    }

    private BoundExpression? Missing(string name, TextSpan span)
    { context.Report("XG3112", "Required Avalonia container-query contract is missing: " + name, span); return null; }
}
