using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

/// <summary>Both binding syntaxes lower through one typed path binder. Runtime reflection is never a fallback for a failed compiled path.</summary>
public sealed class AvaloniaCompiledBindingRule : IXamlMarkupBindingRule
{
    private static readonly XamlAnnotationKey<Dictionary<TextSpan, CompiledBindingResult?>> Results = new("Avalonia.CompiledBindings");

    internal static void ShareResults(ObjectBindingBuilder source, ObjectBindingBuilder target)
    {
        if (source.Annotations.TryGet(Results, out var results)) target.Annotations.Set(Results, results);
    }

    public bool TryBind(BindingContext context, MarkupExtensionSyntax syntax, ITypeSymbol targetType,
        NamespaceScope scope, out BoundExpression? expression)
    {
        expression = null;
        var target = context.Ancestors.FirstOrDefault();
        var type = target == null ? null : context.ResolveType(syntax.Name, scope, syntax.Span, report: false, extension: true);
        if (target == null || type == null || !ShouldCompile(target, type)) return false;
        expression = Bind(context, target, targetType, syntax.Span,
            () => CompiledBindingInputReader.FromMarkup(context, syntax, scope))?.Expression;
        return true;
    }

    public bool TryBindElement(BindingContext context, XamlElementSyntax syntax, ITypeSymbol targetType,
        NamespaceScope parentScope, out BoundExpression? expression)
    {
        expression = null;
        var target = context.Ancestors.FirstOrDefault();
        var type = target == null ? null : context.ResolveType(syntax.Name, parentScope.Push(syntax), syntax.NameSpan, report: false);
        if (target == null || type == null || !ShouldCompile(target, type)) return false;
        var compiledType = context.Types.Find(AvaloniaBindingMetadata.CompiledExtension);
        if (compiledType == null)
        {
            context.Report("XG3202", "CompiledBindingExtension is unavailable.", syntax.Span);
            return true;
        }
        expression = Bind(context, target, targetType, syntax.Span,
            () => CompiledBindingInputReader.FromElement(context, syntax, parentScope, compiledType))?.Expression;
        return true;
    }

    internal static bool ShouldCompile(ObjectBindingBuilder target, INamedTypeSymbol type)
    {
        if (type.HasMetadataName(AvaloniaBindingMetadata.CompiledBinding) || type.HasMetadataName(AvaloniaBindingMetadata.CompiledExtension))
            return true;
        return target.Annotations.TryGet(AvaloniaBindingScope.Key, out var configuration) && configuration.CompileBindings &&
               (type.HasMetadataName(AvaloniaBindingMetadata.ReflectionBinding) || type.HasMetadataName(AvaloniaBindingMetadata.BindingExtension));
    }

    internal static CompiledBindingResult? Bind(BindingContext context, ObjectBindingBuilder target,
        ITypeSymbol targetType, TextSpan span, Func<CompiledBindingInput?> readInput, bool inferDataContext = false)
    {
        // Scope inference precedes assignment binding. Reuse its objects, path and diagnostics.
        if (!target.Annotations.TryGet(Results, out var results))
            target.Annotations.Set(Results, results = new());
        if (results.TryGetValue(span, out var known)) return known;
        // A property may declare that its own binding supplies the collection type.
        results.Add(span, null);
        var input = readInput();
        XamlElementSyntax? consumer = null;
        if (!inferDataContext && input != null && target.Annotations.TryGet(AvaloniaBindingScope.Key, out var configuration) && configuration.HasDataTypeMetadata &&
            context.PropertyScope is { } property && ReferenceEquals(property.Target, target))
        {
            consumer = AvaloniaItemInferenceDependencies.Consumer(context, target, property.Member);
            inferDataContext = consumer != null;
        }
        using var metadataScope = consumer == null ? null : AvaloniaNamedSourceScopes.Get(context).EnterMetadata(consumer);
        var result = input == null ? null : BindCore(context, target, targetType, input, inferDataContext);
        results[span] = result;
        return result;
    }

    private static CompiledBindingResult? BindCore(BindingContext context, ObjectBindingBuilder target,
        ITypeSymbol targetType, CompiledBindingInput input, bool inferDataContext)
    {
        var compiledType = context.Types.Find(AvaloniaBindingMetadata.CompiledExtension);
        var span = input.ObjectSyntax.Span;
        if (compiledType == null)
        {
            context.Report("XG3202", "CompiledBindingExtension is unavailable.", span);
            return null;
        }
        var configuration = target.Annotations.TryGet(AvaloniaBindingScope.Key, out var current) ? current : new(null, false);
        var itemType = inferDataContext ? null : AvaloniaItemTypeInference.Read(context);
        var pathScope = input.Path.Scope ?? input.Scope;
        var path = BindingPathParser.ParseAtSource(input.Path.Text, input.Path.Span, context.Syntax.Text, context.Diagnostics.Add, context.Cancellation);
        if (path == null) return null;
        var declaredType = input.DataType == null ? null : AvaloniaBindingScopeRule.ResolveDataType(
            context, input.DataType.Text, input.DataType.Scope ?? input.Scope, input.DataType.Span);
        // Avalonia fixes DataContext paths before the general binding Source/DataType transform.
        ITypeSymbol? sourceType = inferDataContext || input.DataType == null ? itemType ?? configuration.DataType : declaredType;
        if (inferDataContext && sourceType == null && path.Segments.IsEmpty)
        {
            context.Report("XG3209", "DataContext binding inference requires an inherited data type.", input.Path.Span);
            return null;
        }
        var extension = context.Objects.Bind(input.ObjectSyntax, input.ParentScope, compiledType, false,
            target.NameScopeId, ImmutableArray<XamlSyntaxNode>.Empty);
        if (extension == null) return null;
        var source = extension.Assignments.OfType<BoundSetAssignment>().FirstOrDefault(a => a.Member.Name == AvaloniaBindingMetadata.Source)?.Value;
        var sourceOptions = (source != null ? 1 : 0) + (input.ElementName != null ? 1 : 0) + (input.RelativeSource != null ? 1 : 0);
        if (sourceOptions > 1)
        {
            context.Report("XG3210", "Source, ElementName and RelativeSource are mutually exclusive.", span);
            return null;
        }
        if (sourceOptions != 0 && path.Segments.Any(s => s.Kind is BindingPathKind.Self or BindingPathKind.Parent or BindingPathKind.ElementName))
        {
            context.Report("XG3210", "A binding cannot combine a source argument with a source-qualified path.", input.Path.Span);
            return null;
        }
        if (!inferDataContext && input.DataType == null)
        {
            if (source is BoundReferenceExpression namedReference)
                sourceType = new BindingSourceResolver(context).Named(namedReference.Name, namedReference.Span).Type;
            else if (source?.Type is { SpecialType: not SpecialType.System_Object } explicitType)
                sourceType = explicitType;
            else if (source != null && BindingResourceTypeResolver.Find(context, source) is { } resourceType)
                sourceType = resourceType;
        }
        BoundBindingSource? initialSource = null;
        if (input.ElementName != null)
        {
            initialSource = new RelativeBindingSourceBinder(context, target, input.Scope)
                .Element(input.ElementName.Text, input.ElementName.Span);
            if (initialSource == null) return null;
        }
        if (input.RelativeSource != null)
        {
            initialSource = new RelativeBindingSourceBinder(context, target, input.RelativeSource.Scope)
                .Relative(input.RelativeSource.Syntax, sourceType);
            if (initialSource == null) return null;
        }
        var actualTarget = AvaloniaBindingTargetScope.Get(target) ?? targetType;
        var bound = new CompiledBindingPathBinder(context, pathScope, target).Bind(path, sourceType,
            actualTarget.HasMetadataName(AvaloniaBindingMetadata.Command), initialSource);
        if (bound == null) return null;
        var mode = extension.Assignments.OfType<BoundSetAssignment>().FirstOrDefault(a => a.Member.Name == AvaloniaBindingMetadata.Mode)?.Value;
        if (mode is BoundEnumExpression modeExpression && modeExpression.Fields.Any(f => f.Name is "TwoWay" or "OneWayToSource") && !bound.CanWrite)
        {
            context.Report("XG3212", "The compiled binding path is not writable in the requested binding mode.", input.Path.Span);
            return null;
        }
        var pathMember = context.Members.Resolve(compiledType, AvaloniaBindingMetadata.PathMember, input.Scope, span);
        if (pathMember == null) return null;
        extension = extension with { Assignments = extension.Assignments.Insert(0, new BoundSetAssignment(pathMember, bound.Path, input.Path.Span)) };
        var provide = compiledType.GetMembers("ProvideValue").OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Parameters.Length == 1 && context.Types.IsAccessible(m));
        if (provide == null)
        {
            context.Report("XG3202", "The compiled binding value provider is unavailable.", span);
            return null;
        }
        return new(new BoundMarkupExpression(extension, provide, provide.ReturnType, span), bound.ValueType);
    }
}
