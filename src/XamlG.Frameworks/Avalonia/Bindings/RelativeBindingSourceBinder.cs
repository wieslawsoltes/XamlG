using System.Globalization;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal sealed class RelativeBindingSourceBinder(BindingContext context, ObjectBindingBuilder target, NamespaceScope scope)
{
    private readonly BindingExpressionFactory _expressions = new(context);

    public BoundBindingSource? Element(string name, TextSpan span)
    {
        var resolved = new BindingSourceResolver(context, target).Named(name, span);
        var builder = _expressions.New(AvaloniaBindingMetadata.PathBuilder, span);
        if (resolved.Type == null || builder == null) return null;
        builder = _expressions.Call(builder, "ElementName", span,
            new BoundServiceExpression(context.Types.Find(AvaloniaMetadata.NameScopeContract)!, span),
            _expressions.Text(name, span));
        return builder == null ? null : new(builder, resolved.Type, resolved.DataType);
    }

    public BoundBindingSource? Relative(MarkupExtensionSyntax syntax, ITypeSymbol? dataType)
    {
        var span = syntax.Span;
        var type = context.ResolveType(syntax.Name, scope, span, report: false, extension: true);
        if (type == null || !(type.HasMetadataName(AvaloniaBindingMetadata.RelativeSourceType) ||
                              type.HasMetadataName(AvaloniaBindingMetadata.RelativeSourceExtension)))
        {
            context.Report("XG3211", "RelativeSource requires a statically configured RelativeSource.", span);
            return null;
        }
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "Mode", "AncestorType", "AncestorLevel", "Tree" };
        if (syntax.Arguments.Count(a => a.Name == null) > 1 ||
            syntax.Arguments.Any(a => a.Name != null && !allowed.Contains(a.Name)) ||
            syntax.Arguments.Where(a => a.Name != null).GroupBy(a => a.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            context.Report("XG3211", "RelativeSource contains an unknown or duplicate argument.", span);
            return null;
        }
        string? Argument(string name) => syntax.Arguments.FirstOrDefault(a => a.Name == name)?.Value;
        var positionalMode = syntax.Arguments.FirstOrDefault(a => a.Name == null)?.Value;
        if (positionalMode != null && Argument("Mode") != null)
        {
            context.Report("XG3211", "RelativeSource.Mode is assigned more than once.", span);
            return null;
        }
        var mode = Argument("Mode") ?? positionalMode ?? "FindAncestor";
        var builder = _expressions.New(AvaloniaBindingMetadata.PathBuilder, span);
        if (builder == null) return null;
        ITypeSymbol? sourceType;
        ITypeSymbol? rootedDataType = null;
        switch (mode)
        {
            case "DataContext":
                sourceType = dataType;
                break;
            case "Self":
                sourceType = BindingTargetTypeResolver.Resolve(context, target);
                rootedDataType = target.Annotations.TryGet(AvaloniaBindingScope.Key, out var current) ? current.DataType : null;
                builder = _expressions.Call(builder, "Self", span);
                break;
            case "TemplatedParent":
                sourceType = AvaloniaStyleObjectRule.FindTarget(context, target) ??
                             context.Types.Find(AvaloniaBindingMetadata.TemplatedControl);
                builder = _expressions.Call(builder, "TemplatedParent", span);
                break;
            case "FindAncestor":
                var typeText = Argument("AncestorType");
                if (typeText == null)
                {
                    context.Report("XG3211", "FindAncestor requires AncestorType for a compiled binding.", span);
                    return null;
                }
                sourceType = AvaloniaBindingScopeRule.ResolveDataType(context, typeText, scope, span);
                if (sourceType == null) return null;
                var levelText = Argument("AncestorLevel");
                var level = 1;
                if (levelText != null && (!int.TryParse(levelText, NumberStyles.None, CultureInfo.InvariantCulture, out level) || level < 1))
                {
                    context.Report("XG3211", "AncestorLevel must be a positive integer.", span);
                    return null;
                }
                var tree = Argument("Tree") ?? "Visual";
                if (tree is not ("Visual" or "Logical"))
                {
                    context.Report("XG3211", "RelativeSource.Tree must be Visual or Logical.", span);
                    return null;
                }
                var ancestor = context.Ancestors.Where(a => !ReferenceEquals(a, target) &&
                    context.Types.Compilation.ClassifyCommonConversion(a.Type, sourceType).IsImplicit)
                    .Skip(level - 1).FirstOrDefault();
                if (ancestor != null && ancestor.Annotations.TryGet(AvaloniaBindingScope.Key, out var parentScope))
                    rootedDataType = parentScope.DataType;
                builder = _expressions.Call(builder, tree == "Visual" ? "VisualAncestor" : "Ancestor", span,
                    _expressions.Type(sourceType, span), _expressions.Number(level - 1, span));
                break;
            default:
                context.Report("XG3211", "Unknown RelativeSource mode: " + mode, span);
                return null;
        }
        return builder == null ? null : new(builder, sourceType, rootedDataType);
    }
}
