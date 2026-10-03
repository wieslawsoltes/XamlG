using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal sealed class CompiledBindingPathBinder(BindingContext context, NamespaceScope scope, ObjectBindingBuilder target)
{
    private readonly BindingExpressionFactory _expressions = new(context);
    private readonly BindingAccessorBuilder _accessors = new(context);

    public CompiledPathResult? Bind(BindingPathSyntax syntax, ITypeSymbol? sourceType, bool commandTarget = false, BoundBindingSource? initialSource = null)
    {
        BoundExpression? builder = initialSource?.Builder ?? _expressions.New(AvaloniaBindingMetadata.PathBuilder, syntax.Span);
        if (builder == null) return null;
        var type = initialSource?.SourceType ?? sourceType;
        var writable = false;
        INamedTypeSymbol? rootedDataType = initialSource?.DataType;
        var resolver = new BindingSourceResolver(context, target);
        for (var index = 0; index < syntax.Segments.Length; index++)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var segment = syntax.Segments[index];
            switch (segment.Kind)
            {
                case BindingPathKind.Not:
                    builder = _expressions.Call(builder, "Not", segment.Span); break;
                case BindingPathKind.Self:
                    builder = _expressions.Call(builder, "Self", segment.Span);
                    type = BindingTargetTypeResolver.Resolve(context, target);
                    rootedDataType = target.Annotations.TryGet(AvaloniaBindingScope.Key, out var own) ? own.DataType : null;
                    break;
                case BindingPathKind.Parent:
                    var parent = resolver.Parent(segment, scope);
                    if (parent.Type == null) return null;
                    builder = _expressions.Call(builder, "Ancestor", segment.Span, _expressions.Type(parent.Type, segment.Span), _expressions.Number(parent.Level, segment.Span));
                    type = parent.Type; rootedDataType = parent.DataType;
                    break;
                case BindingPathKind.ElementName:
                    var named = resolver.Named(segment.Name, segment.Span);
                    if (named.Type == null) return null;
                    builder = _expressions.Call(builder, "ElementName", segment.Span,
                        new BoundServiceExpression(context.Types.Find(AvaloniaMetadata.NameScopeContract)!, segment.Span), _expressions.Text(segment.Name, segment.Span));
                    type = named.Type; rootedDataType = named.DataType;
                    break;
                case BindingPathKind.Cast:
                    type = context.ResolveType(segment.Name, scope, segment.Span);
                    if (type == null) return null;
                    builder = _expressions.GenericCall(builder, "TypeCast", type, segment.Span);
                    break;
                case BindingPathKind.AttachedProperty:
                    var registered = AvaloniaRegisteredPropertyResolver.Resolve(context, type, segment.Name, scope, segment.Span);
                    if (registered == null) return null;
                    var attached = _accessors.Attached(registered, segment.Span);
                    if (attached == null) return null;
                    builder = _expressions.Call(builder, "Property", segment.Span, attached.PropertyInfo, attached.Factory);
                    type = attached.ValueType; writable = attached.CanWrite;
                    break;
                case BindingPathKind.Property:
                    if (type == null) { MissingDataType(segment); return null; }
                    var property = type.Members(segment.Name).OfType<IPropertySymbol>().FirstOrDefault(p => !p.IsStatic && !p.IsIndexer && p.GetMethod != null && context.Types.IsAccessible(p.GetMethod));
                    var field = property == null ? type.Members(segment.Name).OfType<IFieldSymbol>().FirstOrDefault(f => !f.IsStatic && context.Types.IsAccessible(f)) : null;
                    if (property == null && field == null)
                    {
                        if (commandTarget && index == syntax.Segments.Length - 1)
                        {
                            builder = Command(builder, type, segment);
                            type = context.Types.Find(AvaloniaBindingMetadata.Command); writable = false; break;
                        }
                        context.Report("XG3205", $"Readable member '{segment.Name}' does not exist on '{type.ToDisplayString()}'.", segment.Span); return null;
                    }
                    var accessor = _accessors.Property(type, (ISymbol?)property ?? field!, ImmutableArray<BoundExpression>.Empty, segment.Span);
                    if (accessor == null) return null;
                    builder = _expressions.Call(builder, "Property", segment.Span, accessor.PropertyInfo, accessor.Factory);
                    type = accessor.ValueType; writable = accessor.CanWrite;
                    if (builder != null && segment.Name == AvaloniaBindingMetadata.DataContext && rootedDataType != null)
                    { type = rootedDataType; builder = _expressions.GenericCall(builder, "TypeCast", type, segment.Span); rootedDataType = null; }
                    break;
                case BindingPathKind.Indexer:
                    if (type == null) { MissingDataType(segment); return null; }
                    if (type is IArrayTypeSymbol array)
                    {
                        var indices = new List<BoundExpression>();
                        foreach (var argument in segment.Arguments)
                        {
                            if (!int.TryParse(argument, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                            { context.Report("XG3206", "Array indices must be integer constants.", segment.Span); return null; }
                            indices.Add(_expressions.Number(number, segment.Span));
                        }
                        if (indices.Count != array.Rank) { context.Report("XG3206", "The number of array indices must match its rank.", segment.Span); return null; }
                        builder = _expressions.Call(builder, "ArrayElement", segment.Span,
                            new BoundArrayExpression(indices.ToImmutableArray(), context.Types.Compilation.CreateArrayTypeSymbol(context.Types.Special(SpecialType.System_Int32)), segment.Span), _expressions.Type(array.ElementType, segment.Span));
                        type = array.ElementType; writable = true;
                    }
                    else
                    {
                        var candidates = new List<(IPropertySymbol Property, ImmutableArray<BoundExpression> Arguments, int Score)>();
                        foreach (var candidate in type.Members().OfType<IPropertySymbol>().Where(p => p.IsIndexer && p.GetMethod != null && context.Types.IsAccessible(p.GetMethod) && p.Parameters.Length == segment.Arguments.Length))
                        {
                            var arguments = ImmutableArray.CreateBuilder<BoundExpression>(); var score = 0;
                            for (var i = 0; i < candidate.Parameters.Length; i++)
                            {
                                var text = segment.Arguments[i]; var quoted = text.Length > 1 && text[0] is '\'' or '"' && text[text.Length - 1] == text[0];
                                var converted = context.Values.TryText(quoted ? text.Substring(1, text.Length - 2) : text, candidate.Parameters[i].Type, scope, segment.Span);
                                if (converted == null) break;
                                if (!quoted && candidate.Parameters[i].Type.SpecialType == SpecialType.System_String) score++;
                                arguments.Add(converted);
                            }
                            if (arguments.Count == candidate.Parameters.Length) candidates.Add((candidate, arguments.ToImmutable(), score));
                        }
                        var selected = candidates.OrderBy(c => c.Score).FirstOrDefault();
                        if (selected.Property == null) { context.Report("XG3206", $"No accessible indexer on '{type}' accepts these arguments.", segment.Span); return null; }
                        var indexed = _accessors.Property(type, selected.Property, selected.Arguments, segment.Span);
                        if (indexed == null) return null;
                        builder = _expressions.Call(builder, "Property", segment.Span, indexed.PropertyInfo, indexed.Factory);
                        type = indexed.ValueType; writable = indexed.CanWrite;
                    }
                    break;
                case BindingPathKind.Stream:
                    if (type == null) { MissingDataType(segment); return null; }
                    var task = FindGeneric(type, AvaloniaBindingMetadata.Task);
                    var observable = FindGeneric(type, AvaloniaBindingMetadata.Observable);
                    var stream = task ?? observable;
                    if (stream == null) { context.Report("XG3207", $"'{type}' is not a statically typed task or observable.", segment.Span); return null; }
                    type = stream.TypeArguments[0];
                    builder = _expressions.GenericCall(builder, task != null ? "StreamTask" : "StreamObservable", type, segment.Span); writable = false;
                    break;
                default: throw new InvalidOperationException("Unknown binding path segment.");
            }
            if (builder == null) return null;
        }
        var path = _expressions.Call(builder, "Build", syntax.Span);
        return path == null ? null : new(path, type, writable);
    }

    private BoundExpression? Command(BoundExpression builder, ITypeSymbol type, BindingPathSegment segment)
    {
        var methods = type.Members(segment.Name).OfType<IMethodSymbol>()
            .Where(m => !m.IsStatic && !m.IsGenericMethod && m.Parameters.Length <= 1 && m.Parameters.All(p => p.RefKind == RefKind.None) && context.Types.IsAccessible(m)).ToArray();
        if (methods.Length != 1) { context.Report("XG3208", "A command binding requires one accessible method with zero or one parameter: " + segment.Name, segment.Span); return null; }
        var method = methods[0];
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
        var canMethod = type.Members(canName).OfType<IMethodSymbol>().FirstOrDefault(m => !m.IsStatic && !m.IsGenericMethod && m.ReturnType.SpecialType == SpecialType.System_Boolean &&
            m.Parameters.Length == method.Parameters.Length && m.Parameters.Select((p, i) => SymbolEqualityComparer.Default.Equals(p.Type, method.Parameters[i].Type)).All(v => v) && context.Types.IsAccessible(m));
        var canProperty = type.Members(canName).OfType<IPropertySymbol>().FirstOrDefault(p => !p.IsStatic && p.Type.SpecialType == SpecialType.System_Boolean && p.GetMethod != null && context.Types.IsAccessible(p.GetMethod));
        if (canMethod != null || canProperty != null)
        {
            BoundExpression body = canMethod != null ? new BoundCallExpression(canMethod, receiver, arguments, segment.Span) :
                new BoundPropertyAccessExpression(receiver, canProperty!, ImmutableArray<BoundExpression>.Empty, segment.Span);
            canExecute = new BoundLambdaExpression((INamedTypeSymbol)commandMethod.Parameters[2].Type, ImmutableArray.Create(targetParameter, valueParameter), body, true, segment.Span);
        }
        var dependencies = (canMethod as ISymbol ?? canProperty)?.GetAttributes()
            .Where(a => a.AttributeClass?.HasMetadataName(AvaloniaBindingMetadata.DependsOn) == true)
            .SelectMany(a => a.ConstructorArguments).Where(a => a.Value is string).Select(a => (string)a.Value!).ToList() ?? new List<string>();
        if (canProperty != null) dependencies.Add(canProperty.Name);
        var strings = context.Types.Compilation.CreateArrayTypeSymbol(context.Types.Special(SpecialType.System_String));
        context.Symbols.Add(new(segment.Span, method, "binding-command"));
        return _expressions.Call(builder, "Command", segment.Span, _expressions.Text(method.Name, segment.Span), execute, canExecute,
            new BoundArrayExpression(dependencies.Distinct(StringComparer.Ordinal).Select(d => _expressions.Text(d, segment.Span)).ToImmutableArray(), strings, segment.Span));
    }
    private void MissingDataType(BindingPathSegment segment) => context.Report("XG3209", "Compiled binding needs x:DataType, an explicit DataType, or a statically typed source before '" + segment.Name + "'.", segment.Span);
    private static INamedTypeSymbol? FindGeneric(ITypeSymbol type, string name)
    {
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            if (current.OriginalDefinition.HasMetadataName(name)) return current;
        return type.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.HasMetadataName(name));
    }
}
