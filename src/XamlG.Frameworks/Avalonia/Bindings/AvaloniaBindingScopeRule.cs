using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

public sealed class AvaloniaBindingScopeRule(bool compileBindingsByDefault = true) : IXamlObjectBindingRule, IXamlBindingRule
{
    public void Initialize(BindingContext context, ObjectBindingBuilder target)
    {
        var parent = context.Ancestors.Skip(1).FirstOrDefault();
        var inherited = parent != null && parent.Annotations.TryGet(AvaloniaBindingScope.Key, out var known) ? known : new(null, compileBindingsByDefault);
        var dataType = inherited.DataType;
        var declared = target.Scope.Directive(target.Syntax, AvaloniaBindingMetadata.DataType);
        var source = declared == null ? (string?)null : declared.Value;
        var span = declared?.ValueSpan ?? target.Syntax.NameSpan;
        if (source == null && (target.Type.HasMetadataName(AvaloniaBindingMetadata.DataTemplate) || target.Type.HasMetadataName(AvaloniaBindingMetadata.TreeDataTemplate)))
        {
            var attribute = AvaloniaStyleObjectRule.TextMember(target.Syntax, AvaloniaBindingMetadata.DataType);
            if (attribute is { } item) { source = item.Text; span = item.Span; }
            else dataType = null; // A new template must not silently inherit its owner's view-model type.
        }
        if (source != null)
            dataType = ResolveDataType(context, source, target.Scope, span);
        var compile = inherited.CompileBindings;
        var directive = target.Scope.Directive(target.Syntax, AvaloniaBindingMetadata.CompileBindings);
        if (directive != null)
        {
            if (!bool.TryParse(directive.Value, out compile)) context.Report("XG3201", "x:CompileBindings requires True or False.", directive.ValueSpan);
        }
        target.Annotations.Set(AvaloniaBindingScope.Key, new(dataType, compile));
    }
    public void Complete(BindingContext context, ObjectBindingBuilder target) { }
    public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
    {
        var name = scope.Expand(attribute.Name, true);
        return name.Namespace != null && XamlNames.IsLanguage(name.Namespace) && name.LocalName is AvaloniaBindingMetadata.DataType or AvaloniaBindingMetadata.CompileBindings;
    }
    internal static INamedTypeSymbol? ResolveDataType(BindingContext context, string text, NamespaceScope scope, TextSpan span)
    {
        if (text.StartsWith("{", StringComparison.Ordinal))
            return (context.Values.BindText(text, context.Types.Find(ClrNames.Type)!, scope, span) as BoundTypeExpression)?.ReferencedType as INamedTypeSymbol;
        return context.ResolveType(text, scope, span);
    }
}
