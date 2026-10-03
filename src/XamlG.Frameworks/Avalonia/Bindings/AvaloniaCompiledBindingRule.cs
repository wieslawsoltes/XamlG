using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

/// <summary>Constructs compiled paths from Roslyn members and generated delegates. No runtime path parsing or reflection fallback.</summary>
public sealed class AvaloniaCompiledBindingRule : IXamlMarkupBindingRule
{
    public bool TryBind(BindingContext context, MarkupExtensionSyntax syntax, ITypeSymbol targetType, NamespaceScope scope, out BoundExpression? expression)
    {
        expression = null;
        var target = context.Ancestors.FirstOrDefault();
        if (target == null) return false;
        var type = context.ResolveType(syntax.Name, scope, syntax.Span, report: false, extension: true);
        if (type == null) return false;
        var explicitCompiled = type.HasMetadataName(AvaloniaBindingMetadata.CompiledBinding) || type.HasMetadataName(AvaloniaBindingMetadata.CompiledExtension);
        var regular = type.HasMetadataName(AvaloniaBindingMetadata.ReflectionBinding) || type.HasMetadataName(AvaloniaBindingMetadata.BindingExtension);
        var configuration = target.Annotations.TryGet(AvaloniaBindingScope.Key, out var current) ? current : new(null, false);
        if (!explicitCompiled && (!regular || !configuration.CompileBindings)) return false;
        var compiledType = context.Types.Find(AvaloniaBindingMetadata.CompiledExtension);
        if (compiledType == null) { context.Report("XG3202", "CompiledBindingExtension is unavailable.", syntax.Span); return true; }
        var positional = syntax.Arguments.Where(a => a.Name == null).ToArray();
        var namedPath = syntax.Arguments.FirstOrDefault(a => a.Name == AvaloniaBindingMetadata.PathMember);
        if (positional.Length > 1 || positional.Length != 0 && namedPath != null)
        { context.Report("XG3210", "A binding path may be specified only once.", syntax.Span); return true; }
        var pathArgument = namedPath ?? positional.FirstOrDefault();
        var path = pathArgument?.Value ?? string.Empty;
        var pathSpan = pathArgument?.Span ?? syntax.Span;
        var namedElement = syntax.Arguments.FirstOrDefault(a => a.Name == AvaloniaBindingMetadata.ElementName);
        var relative = syntax.Arguments.FirstOrDefault(a => a.Name == AvaloniaBindingMetadata.RelativeSource);
        if (namedElement != null && relative != null)
        { context.Report("XG3210", "A binding cannot combine ElementName and RelativeSource.", syntax.Span); return true; }
        if (namedElement != null) path = "#" + namedElement.Value + (path.Length == 0 ? string.Empty : "." + path);
        if (relative != null)
        {
            var relativeSyntax = MarkupExtensionParser.Parse(relative.Value, relative.Span, context.Diagnostics.Add);
            var mode = relativeSyntax?.Arguments.FirstOrDefault(a => a.Name == null || a.Name == "Mode")?.Value;
            if (mode == "Self") path = "$self" + (path.Length == 0 ? string.Empty : "." + path);
            else
            {
                context.Report("XG3211", "This RelativeSource form requires an explicit compiled source path; use $parent[Type] or $self.", relative.Span);
                return true;
            }
        }
        var parsed = BindingPathParser.Parse(path, pathSpan, context.Diagnostics.Add, context.Cancellation);
        if (parsed == null) return true;
        var dataType = syntax.Arguments.FirstOrDefault(a => a.Name == AvaloniaBindingMetadata.DataType);
        ITypeSymbol? sourceType = dataType == null ? configuration.DataType : AvaloniaBindingScopeRule.ResolveDataType(context, dataType.Value, scope, dataType.Span);
        var filtered = syntax.Arguments.Where(a => a.Name != null && a.Name is not AvaloniaBindingMetadata.PathMember and not AvaloniaBindingMetadata.DataType and not AvaloniaBindingMetadata.ElementName and not AvaloniaBindingMetadata.RelativeSource)
            .Select(a => new XamlAttributeSyntax(a.Name!, a.Value, a.Span, a.Span, a.Span, '"')).ToImmutableArray();
        var element = new XamlElementSyntax(syntax.Name, syntax.Span, syntax.Span, new(syntax.Span.End, 0), filtered, ImmutableArray<XamlSyntaxNode>.Empty, true, syntax.Span);
        var extension = context.Objects.Bind(element, scope, compiledType, false, target.NameScopeId, ImmutableArray<XamlSyntaxNode>.Empty);
        if (extension == null) return true;
        if (dataType == null && extension.Assignments.OfType<BoundSetAssignment>().FirstOrDefault(a => a.Member.Name == AvaloniaBindingMetadata.Source)?.Value is { Type: { } source } && source.SpecialType != SpecialType.System_Object)
            sourceType = source;
        var bound = new CompiledBindingPathBinder(context, scope, target).Bind(parsed, sourceType, targetType.HasMetadataName(AvaloniaBindingMetadata.Command));
        if (bound == null) return true;
        var modeText = syntax.Arguments.FirstOrDefault(a => a.Name == AvaloniaBindingMetadata.Mode)?.Value;
        if (modeText is "TwoWay" or "OneWayToSource" && !bound.CanWrite)
        { context.Report("XG3212", "The compiled binding path is not writable in the requested binding mode.", pathSpan); return true; }
        var pathMember = context.Members.Resolve(compiledType, AvaloniaBindingMetadata.PathMember, scope, syntax.Span);
        if (pathMember == null) return true;
        extension = extension with { Assignments = extension.Assignments.Insert(0, new BoundSetAssignment(pathMember, bound.Path, pathSpan)) };
        var provide = compiledType.GetMembers("ProvideValue").OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 1 && context.Types.IsAccessible(m));
        if (provide == null) { context.Report("XG3202", "The compiled binding value provider is unavailable.", syntax.Span); return true; }
        expression = new BoundMarkupExpression(extension, provide, provide.ReturnType, syntax.Span);
        return true;
    }
}
