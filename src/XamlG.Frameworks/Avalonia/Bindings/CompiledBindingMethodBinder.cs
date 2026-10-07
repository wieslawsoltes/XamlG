using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal sealed class CompiledBindingMethodBinder(BindingContext context)
{
    private readonly BindingExpressionFactory _expressions = new(context);

    public BoundExpression? Bind(BoundExpression builder, ITypeSymbol type, BindingPathSegment segment, bool command)
    {
        var methods = type.Members(segment.Name).OfType<IMethodSymbol>()
            .Where(m => !m.IsStatic && !m.IsGenericMethod && m.Parameters.Length <= 1 && m.Parameters.All(p => p.RefKind == RefKind.None) && context.Types.IsAccessible(m)).ToArray();
        var oneParameter = methods.Where(method => method.Parameters.Length == 1)
            .GroupBy(method => method.Parameters[0].Type, SymbolEqualityComparer.Default).Select(group => group.First()).ToArray();
        var method = oneParameter.FirstOrDefault(method => method.Parameters[0].Type.SpecialType == SpecialType.System_Object)
            ?? (oneParameter.Length == 1 ? oneParameter[0] : oneParameter.Length == 0 ? methods.FirstOrDefault() : null);
        if (method == null) { context.Report("XG3208", "The binding method requires one unambiguous zero/one-parameter overload: " + segment.Name, segment.Span); return null; }
        context.Symbols.Add(new(segment.Span, method, command ? "binding-command" : "binding-method"));
        if (!command) return Delegate(builder, method, segment);
        var commandMethod = builder.Type!.Members("Command").OfType<IMethodSymbol>().Single(m => m.Parameters.Length == 4);
        var objectType = context.Types.Special(SpecialType.System_Object);
        var targetParameter = new BoundParameterExpression("target", objectType, segment.Span);
        var valueParameter = new BoundParameterExpression("value", objectType, segment.Span);
        var receiver = new BoundCastExpression(targetParameter, method.ContainingType, segment.Span);
        var arguments = method.Parameters.Length == 0 ? ImmutableArray<BoundExpression>.Empty : ImmutableArray.Create<BoundExpression>(new BoundCastExpression(valueParameter, method.Parameters[0].Type, segment.Span));
        BoundExpression execute = new BoundLambdaExpression((INamedTypeSymbol)commandMethod.Parameters[1].Type,
            ImmutableArray.Create(targetParameter, valueParameter), new BoundCallExpression(method, receiver, arguments, segment.Span), true, segment.Span);
        BoundExpression canExecute = new BoundConstantExpression(null, commandMethod.Parameters[2].Type, segment.Span);
        var canName = "Can" + method.Name;
        var canMethod = method.ContainingType.Members(canName).OfType<IMethodSymbol>().FirstOrDefault(m => !m.IsStatic && !m.IsGenericMethod &&
            m.ReturnType.SpecialType == SpecialType.System_Boolean && m.Parameters.Length == 1 &&
            m.Parameters[0].Type.SpecialType == SpecialType.System_Object && context.Types.IsAccessible(m));
        // Retain the existing typed CanExecute extension when the upstream object contract is absent.
        canMethod ??= type.Members(canName).OfType<IMethodSymbol>().FirstOrDefault(m => !m.IsStatic && !m.IsGenericMethod && m.ReturnType.SpecialType == SpecialType.System_Boolean &&
            m.Parameters.Length == method.Parameters.Length && m.Parameters.Select((p, i) => SymbolEqualityComparer.Default.Equals(p.Type, method.Parameters[i].Type)).All(v => v) && context.Types.IsAccessible(m));
        var canProperty = type.Members(canName).OfType<IPropertySymbol>().FirstOrDefault(p => !p.IsStatic && p.Type.SpecialType == SpecialType.System_Boolean && p.GetMethod != null && context.Types.IsAccessible(p.GetMethod));
        if (canMethod != null || canProperty != null)
        {
            var canReceiver = new BoundCastExpression(targetParameter, (canMethod as ISymbol ?? canProperty!).ContainingType, segment.Span);
            var canArguments = canMethod?.Parameters.Length == 1
                ? ImmutableArray.Create<BoundExpression>(new BoundCastExpression(valueParameter, canMethod.Parameters[0].Type, segment.Span))
                : ImmutableArray<BoundExpression>.Empty;
            BoundExpression body = canMethod != null ? new BoundCallExpression(canMethod, canReceiver, canArguments, segment.Span) :
                new BoundPropertyAccessExpression(canReceiver, canProperty!, ImmutableArray<BoundExpression>.Empty, segment.Span);
            canExecute = new BoundLambdaExpression((INamedTypeSymbol)commandMethod.Parameters[2].Type, ImmutableArray.Create(targetParameter, valueParameter), body, true, segment.Span);
        }
        var dependencies = (canMethod as ISymbol ?? canProperty)?.GetAttributes()
            .Where(a => a.AttributeClass?.HasMetadataName(AvaloniaBindingMetadata.DependsOn) == true)
            .SelectMany(a => a.ConstructorArguments).Where(a => a.Value is string).Select(a => (string)a.Value!).ToList() ?? new List<string>();
        if (canProperty != null) dependencies.Add(canProperty.Name);
        var strings = context.Types.Compilation.CreateArrayTypeSymbol(context.Types.Special(SpecialType.System_String));
        return _expressions.Call(builder, "Command", segment.Span, _expressions.Text(method.Name, segment.Span), execute, canExecute,
            new BoundArrayExpression(dependencies.Distinct(StringComparer.Ordinal).Select(d => _expressions.Text(d, segment.Span)).ToImmutableArray(), strings, segment.Span));
    }

    private BoundExpression? Delegate(BoundExpression builder, IMethodSymbol method, BindingPathSegment segment)
    {
        var arguments = method.Parameters.Select(parameter => parameter.Type).ToList();
        INamedTypeSymbol? delegateType;
        if (method.ReturnsVoid)
            delegateType = arguments.Count == 0 ? context.Types.Find("System.Action") : context.Types.Find("System.Action`1")?.Construct(arguments.ToArray());
        else
        {
            arguments.Add(method.ReturnType);
            delegateType = context.Types.Find("System.Func`" + arguments.Count)?.Construct(arguments.ToArray());
        }
        var handleType = context.Types.Find("System.RuntimeMethodHandle");
        var typeHandle = context.Types.Find(ClrNames.Type)?.GetMembers("TypeHandle").OfType<IPropertySymbol>().FirstOrDefault();
        if (delegateType == null || handleType == null || typeHandle == null)
        { context.Report("XG3202", "Method binding requires runtime method and delegate handles.", segment.Span); return null; }
        var inputs = new List<BoundExpression>
        {
            new BoundMethodHandleExpression(method, handleType, segment.Span),
            new BoundPropertyAccessExpression(_expressions.Type(delegateType, segment.Span), typeHandle, ImmutableArray<BoundExpression>.Empty, segment.Span)
        };
        if (segment.AcceptsNull) inputs.Add(_expressions.Constant(true, context.Types.Special(SpecialType.System_Boolean), segment.Span));
        return _expressions.Call(builder, "Method", segment.Span, inputs.ToArray());
    }
}
