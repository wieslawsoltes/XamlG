using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

/// <summary>Both binding syntaxes lower through one typed path binder. Runtime reflection is never a fallback for a failed compiled path.</summary>
public sealed class AvaloniaCompiledBindingRule : IXamlMarkupBindingRule
{
    public bool TryBind(BindingContext context, MarkupExtensionSyntax syntax, ITypeSymbol targetType,
        NamespaceScope scope, out BoundExpression? expression)
    {
        expression = null;
        var target = context.Ancestors.FirstOrDefault();
        var type = target == null ? null : context.ResolveType(syntax.Name, scope, syntax.Span, report: false, extension: true);
        if (target == null || type == null || !ShouldCompile(target, type)) return false;
        var input = CompiledBindingInputReader.FromMarkup(context, syntax, scope);
        if (input != null) expression = BindCore(context, target, targetType, input);
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
        var input = CompiledBindingInputReader.FromElement(context, syntax, parentScope, compiledType);
        if (input != null) expression = BindCore(context, target, targetType, input);
        return true;
    }

    private static bool ShouldCompile(ObjectBindingBuilder target, INamedTypeSymbol type)
    {
        if (type.HasMetadataName(AvaloniaBindingMetadata.CompiledBinding) || type.HasMetadataName(AvaloniaBindingMetadata.CompiledExtension))
            return true;
        return target.Annotations.TryGet(AvaloniaBindingScope.Key, out var configuration) && configuration.CompileBindings &&
               (type.HasMetadataName(AvaloniaBindingMetadata.ReflectionBinding) || type.HasMetadataName(AvaloniaBindingMetadata.BindingExtension));
    }

    private static BoundExpression? BindCore(BindingContext context, ObjectBindingBuilder target,
        ITypeSymbol targetType, CompiledBindingInput input)
    {
        var compiledType = context.Types.Find(AvaloniaBindingMetadata.CompiledExtension);
        var span = input.ObjectSyntax.Span;
        if (compiledType == null)
        {
            context.Report("XG3202", "CompiledBindingExtension is unavailable.", span);
            return null;
        }
        var configuration = target.Annotations.TryGet(AvaloniaBindingScope.Key, out var current) ? current : new(null, false);
        var pathScope = input.Path.Scope ?? input.Scope;
        var path = BindingPathParser.Parse(input.Path.Text, input.Path.Span, context.Diagnostics.Add, context.Cancellation);
        if (path == null) return null;
        ITypeSymbol? sourceType = input.DataType == null ? configuration.DataType :
            AvaloniaBindingScopeRule.ResolveDataType(context, input.DataType.Text, input.DataType.Scope ?? input.Scope, input.DataType.Span);
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
        if (input.DataType == null)
        {
            if (source is BoundReferenceExpression namedReference)
                sourceType = new BindingSourceResolver(context, target).Named(namedReference.Name, namedReference.Span).Type;
            else if (source?.Type is { SpecialType: not SpecialType.System_Object } explicitType)
                sourceType = explicitType;
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
        return new BoundMarkupExpression(extension, provide, provide.ReturnType, span);
    }
}
