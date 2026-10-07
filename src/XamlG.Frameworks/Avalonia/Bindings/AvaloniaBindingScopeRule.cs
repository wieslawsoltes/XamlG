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
        var metadata = AvaloniaDataTypeMetadata.Read(context, target);
        if (metadata.HasDirective || metadata.Type != null) dataType = metadata.Type;
        else if (AvaloniaStyleScope.Is(target.Type, AvaloniaBindingMetadata.DataTemplateContract))
            dataType = null; // Every IDataTemplate owns a separate data-context type scope.
        else
        {
            var contextElement = target.Syntax.Children.OfType<XamlElementSyntax>().FirstOrDefault(e =>
                e.LocalName.EndsWith("." + AvaloniaBindingMetadata.DataContext, StringComparison.Ordinal));
            var values = contextElement?.Children.OfType<XamlElementSyntax>().ToArray();
            if (contextElement != null && values is { Length: 1 })
            {
                var contextScope = target.Scope.Push(contextElement);
                var inferred = context.Values.PeekNodeType(values[0], contextScope);
                var binding = context.Types.Find(AvaloniaMetadata.BindingBase);
                if (inferred is INamedTypeSymbol named && (binding == null || !context.Types.Compilation.ClassifyCommonConversion(named, binding).IsImplicit))
                    dataType = named;
            }
        }
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
        if (name.Namespace == null || !XamlNames.IsLanguage(name.Namespace) || name.LocalName is not (AvaloniaBindingMetadata.DataType or AvaloniaBindingMetadata.CompileBindings)) return false;
        if (name.LocalName == AvaloniaBindingMetadata.DataType && target.Annotations.TryGet(AvaloniaDataTypeMetadata.MappedDirective, out var property))
        {
            var member = context.Members.Resolve(target.Type, property, scope, attribute.NameSpan);
            context.Members.BindNodes(target, member, new[] { new XamlTextSyntax(attribute.Value, false, attribute.ValueSpan) }, scope, attribute.Span);
        }
        return true;
    }
    internal static INamedTypeSymbol? ResolveDataType(BindingContext context, string text, NamespaceScope scope, TextSpan span)
    {
        if (text.StartsWith("{", StringComparison.Ordinal))
            return (context.Values.BindText(text, context.Types.Find(ClrNames.Type)!, scope, span) as BoundTypeExpression)?.ReferencedType as INamedTypeSymbol;
        return context.ResolveType(text, scope, span);
    }
}
