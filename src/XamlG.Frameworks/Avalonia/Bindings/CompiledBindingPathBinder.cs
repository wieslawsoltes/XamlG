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
        ITypeSymbol? rootedDataType = initialSource?.DataType;
        var resolver = new BindingSourceResolver(context);
        for (var index = 0; index < syntax.Segments.Length; index++)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var segment = syntax.Segments[index];
            var sourceDataType = rootedDataType;
            // Only an immediately preceding name/ancestor node supplies metadata. Negation
            // transforms the completed path rather than introducing an intermediate value.
            if (segment.Kind != BindingPathKind.Not) rootedDataType = null;
            switch (segment.Kind)
            {
                case BindingPathKind.Not:
                    builder = _expressions.Call(builder, "Not", segment.Span); break;
                case BindingPathKind.Self:
                    builder = _expressions.Call(builder, "Self", segment.Span);
                    type = BindingTargetTypeResolver.Resolve(context, target);
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
                    var reference = AvaloniaRegisteredPropertyResolver.Resolve(context, type, segment.Name, scope, segment.Span);
                    if (reference == null) return null;
                    if (reference is not RegisteredProperty registered)
                    { context.Report("XG3205", "An attached-property path requires a registered property field.", segment.Span); return null; }
                    var attached = _accessors.Registered(registered, segment.Span);
                    if (attached == null) return null;
                    builder = Property(builder, attached, segment);
                    type = attached.ValueType; writable = attached.CanWrite;
                    break;
                case BindingPathKind.Property:
                    if (type == null) { MissingDataType(segment); return null; }
                    var property = type.Members(segment.Name).OfType<IPropertySymbol>().FirstOrDefault(p => !p.IsStatic && !p.IsIndexer && p.GetMethod != null && context.Types.IsAccessible(p.GetMethod));
                    var registeredProperty = AvaloniaRegisteredPropertyResolver.Find(context, type, segment.Name);
                    if (registeredProperty != null)
                    {
                        context.Symbols.Add(new(segment.Span, (ISymbol?)property ?? registeredProperty.Declaration, "binding-member"));
                        var registeredAccessor = _accessors.Registered(registeredProperty, segment.Span);
                        if (registeredAccessor == null) return null;
                        builder = Property(builder, registeredAccessor, segment);
                        type = registeredAccessor.ValueType; writable = registeredAccessor.CanWrite;
                        if (builder != null && sourceDataType != null &&
                            registeredProperty.FieldName == AvaloniaBindingMetadata.DataContext + AvaloniaMetadata.PropertySuffix &&
                            registeredProperty.Declaration.ContainingType.HasMetadataName(AvaloniaStyleMetadata.StyledElement))
                        { type = sourceDataType; builder = _expressions.GenericCall(builder, "TypeCast", type, segment.Span); }
                        break;
                    }
                    var field = property == null ? type.Members(segment.Name).OfType<IFieldSymbol>().FirstOrDefault(f => !f.IsStatic && context.Types.IsAccessible(f)) : null;
                    if (property == null && field == null)
                    {
                        if (type.Members(segment.Name).OfType<IMethodSymbol>().Any())
                        {
                            var command = commandTarget && index == syntax.Segments.Length - 1;
                            builder = new CompiledBindingMethodBinder(context).Bind(builder, type, segment, command);
                            type = context.Types.Find(command ? AvaloniaBindingMetadata.Command : "System.Delegate"); writable = false; break;
                        }
                        context.Report("XG3205", $"Readable member '{segment.Name}' does not exist on '{type.ToDisplayString()}'.", segment.Span); return null;
                    }
                    var accessor = _accessors.Property(type, (ISymbol?)property ?? field!, ImmutableArray<BoundExpression>.Empty, segment.Span);
                    if (accessor == null) return null;
                    builder = Property(builder, accessor, segment);
                    type = accessor.ValueType; writable = accessor.CanWrite;
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
                    var stream = observable ?? task;
                    if (stream == null) { context.Report("XG3207", $"'{type}' is not a statically typed task or observable.", segment.Span); return null; }
                    type = stream.TypeArguments[0];
                    builder = _expressions.GenericCall(builder, observable != null ? "StreamObservable" : "StreamTask", type, segment.Span); writable = false;
                    break;
                default: throw new InvalidOperationException("Unknown binding path segment.");
            }
            if (builder == null) return null;
        }
        var path = _expressions.Call(builder, "Build", syntax.Span);
        return path == null ? null : new(path, type, writable);
    }

    private BoundExpression? Property(BoundExpression builder, BindingAccessor accessor, BindingPathSegment segment) => segment.AcceptsNull
        ? _expressions.Call(builder, "Property", segment.Span, accessor.PropertyInfo, accessor.Factory,
            _expressions.Constant(true, context.Types.Special(SpecialType.System_Boolean), segment.Span))
        : _expressions.Call(builder, "Property", segment.Span, accessor.PropertyInfo, accessor.Factory);

    private void MissingDataType(BindingPathSegment segment) => context.Report("XG3209", "Compiled binding needs x:DataType, an explicit DataType, or a statically typed source before '" + segment.Name + "'.", segment.Span);
    private static INamedTypeSymbol? FindGeneric(ITypeSymbol type, string name)
    {
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            if (current.OriginalDefinition.HasMetadataName(name)) return current;
        return type.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.HasMetadataName(name));
    }
}
