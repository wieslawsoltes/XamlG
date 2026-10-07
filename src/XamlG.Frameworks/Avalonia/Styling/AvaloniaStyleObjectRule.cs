using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Bindings;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

public sealed class AvaloniaStyleObjectRule : IXamlObjectBindingRule
{
    public void Initialize(BindingContext context, ObjectBindingBuilder target)
    {
        var parent = context.Ancestors.Skip(1).FirstOrDefault();
        var control = context.Types.Find(AvaloniaMetadata.Control);
        var templateContract = control == null ? null : context.Types.Find("Avalonia.Controls.ITemplate`1")?.Construct(control);
        if (parent != null && templateContract != null && parent.Syntax.Children.Contains(target.Syntax) &&
            parent.Annotations.TryGet(AvaloniaStyleAnnotations.SetterTarget, out var detachedTarget) &&
            parent.Annotations.TryGet(AvaloniaStyleAnnotations.SetterProperty, out var detachedProperty) &&
            !context.Types.Compilation.ClassifyCommonConversion(detachedProperty.ValueType, templateContract).IsImplicit &&
            context.Types.Compilation.ClassifyCommonConversion(target.Type, templateContract).IsImplicit)
            target.Annotations.Set(AvaloniaStyleAnnotations.DetachedTemplateTarget, detachedTarget);
        var inherited = FindTarget(context, target);
        var declared = target.Scope.Directive(target.Syntax, AvaloniaStyleMetadata.SetterTargetType);
        var declaredType = declared == null ? null : AvaloniaBindingScopeRule.ResolveDataType(context, declared.Value, target.Scope, declared.ValueSpan);
        if (AvaloniaStyleScope.Is(target.Type, AvaloniaStyleMetadata.Style))
        {
            var source = AvaloniaSelectorSource.Read(context, target);
            if (source is { } selector && !string.IsNullOrWhiteSpace(selector.Text))
            {
                var syntax = AvaloniaSelectorParser.Parse(selector.Text, selector.Span, context.Diagnostics.Add, context.Cancellation);
                var bound = syntax == null ? null : new AvaloniaSelectorBinder(context, selector.Scope, inherited, FindNestingSelector(context, target)).Bind(syntax);
                if (bound != null)
                {
                    target.Annotations.Set(AvaloniaStyleAnnotations.Selector, bound);
                    if (bound.TargetType != null) target.Annotations.Set(AvaloniaStyleAnnotations.TargetType, bound.TargetType);
                    target.Annotations.Set(AvaloniaStyleAnnotations.SetterScope, new(bound.TargetType, HasComplexActivator(target)));
                }
            }
            else
            {
                var owner = FindStyleOwner(context, target);
                if (owner != null)
                {
                    target.Annotations.Set(AvaloniaStyleAnnotations.TargetType, owner);
                    target.Annotations.Set(AvaloniaStyleAnnotations.SetterScope, new(owner, source != null));
                }
                if (source != null && context.Types.Find(AvaloniaStyleMetadata.Selector) is { } selectorType)
                    target.Annotations.Set(AvaloniaStyleAnnotations.Selector, new BoundSelector(new BoundConstantExpression(null, selectorType, source.Span), owner));
            }
        }
        else if (AvaloniaStyleScope.Is(target.Type, AvaloniaStyleMetadata.ControlTheme) || AvaloniaStyleScope.IsTemplate(target.Type))
        {
            // Always determine the nearest semantic owner. An outer template's target
            // must not shadow a concrete control receiving a nested template.
            var theme = AvaloniaStyleScope.Is(target.Type, AvaloniaStyleMetadata.ControlTheme);
            if (!theme)
                inherited = BindingTargetTypeResolver.FindTemplateOwner(context, target);
            var hasTarget = AvaloniaStyleTargetType.TryRead(context, target, out var type);
            if (!hasTarget)
            {
                if (theme) context.Report("XG3116", "ControlTheme requires an explicit TargetType.", target.Syntax.NameSpan);
                else type = inherited ?? context.Types.Find(AvaloniaMetadata.Control);
            }
            if (type != null)
            {
                target.Annotations.Set(AvaloniaStyleAnnotations.TargetType, type);
                if (!theme) target.Annotations.Set(AvaloniaStyleAnnotations.TemplateTarget, type);
                if (theme) target.Annotations.Set(AvaloniaStyleAnnotations.SetterScope, new(type, false));
            }
        }
        else if (AvaloniaStyleScope.Is(target.Type, AvaloniaStyleMetadata.Setter))
        {
            BindSetter(context, target, declaredType);
        }
        // The directive wraps the inferred style metadata and is nearest to its setters.
        if (declaredType != null)
        {
            target.Annotations.Set(AvaloniaStyleAnnotations.TargetType, declaredType);
            target.Annotations.Set(AvaloniaStyleAnnotations.SetterScope, new(declaredType, HasComplexActivator(target)));
        }
    }

    public void Complete(BindingContext context, ObjectBindingBuilder target)
    {
        if (!AvaloniaStyleScope.Is(target.Type, AvaloniaStyleMetadata.Setter)) return;
        // Property precedes Value regardless of XML attribute order: ISetterValue.Initialize
        // observes the registered property when the Value setter is called.
        var property = target.Assignments.OfType<BoundSetAssignment>().FirstOrDefault(a => a.Member.Name == AvaloniaStyleMetadata.PropertyMember);
        if (property != null) { target.Assignments.Remove(property); target.Assignments.Insert(0, property); }
    }

    private static BoundSelector? FindNestingSelector(BindingContext context, ObjectBindingBuilder current)
    {
        foreach (var ancestor in context.Ancestors)
        {
            if (ReferenceEquals(current, ancestor)) continue;
            if (ancestor.Annotations.TryGet(AvaloniaStyleAnnotations.Selector, out var selector)) return selector;
            if (AvaloniaStyleScope.Is(ancestor.Type, AvaloniaStyleMetadata.ControlTheme) ||
                AvaloniaStyleScope.IsTemplate(ancestor.Type)) break;
        }
        return null;
    }

    private static INamedTypeSymbol? FindStyleOwner(BindingContext context, ObjectBindingBuilder current)
    {
        var parent = context.Ancestors.FirstOrDefault(ancestor => !ReferenceEquals(ancestor, current) && !AvaloniaStyleScope.Is(ancestor.Type, AvaloniaStyleMetadata.Styles));
        if (parent == null) return null;
        if (AvaloniaStyleScope.Is(parent.Type, AvaloniaStyleMetadata.StyledElement)) return parent.Type;
        if (AvaloniaStyleScope.Is(parent.Type, AvaloniaStyleMetadata.ControlTheme))
            context.Report("XG3116", "A Style inside a ControlTheme requires a selector.", current.Syntax.NameSpan);
        return null;
    }

    internal static INamedTypeSymbol? FindTarget(BindingContext context, ObjectBindingBuilder current)
    {
        foreach (var ancestor in context.Ancestors)
            if (!ReferenceEquals(current, ancestor) && ancestor.Annotations.TryGet(AvaloniaStyleAnnotations.TargetType, out var type)) return type;
        return null;
    }

    internal static (string Text, TextSpan Span)? TextMember(XamlElementSyntax element, string name)
    {
        var attribute = element.Attributes.FirstOrDefault(a => a.Name == name || a.LocalName.EndsWith("." + name, StringComparison.Ordinal));
        if (attribute != null) return (attribute.Value, attribute.ValueSpan);
        var property = element.Children.OfType<XamlElementSyntax>().FirstOrDefault(e => e.LocalName.EndsWith("." + name, StringComparison.Ordinal));
        if (property == null || property.Children.OfType<XamlElementSyntax>().Any()) return null;
        return (string.Concat(property.Children.OfType<XamlTextSyntax>().Select(t => t.Value)).Trim(), property.Span);
    }

    private static void BindSetter(BindingContext context, ObjectBindingBuilder target, INamedTypeSymbol? declaredType)
    {
        var scope = declaredType == null ? context.Ancestors.Where(ancestor => !ReferenceEquals(ancestor, target))
            .Select(ancestor => ancestor.Annotations.TryGet(AvaloniaStyleAnnotations.SetterScope, out var value) ? value : null).FirstOrDefault(value => value != null)
            : new AvaloniaSetterScope(declaredType, HasComplexActivator(target));
        if (scope?.TargetType == null)
        { context.Report("XG3102", "Setter requires a style target type or x:SetterTargetType.", target.Syntax.NameSpan); return; }
        target.Annotations.Set(AvaloniaStyleAnnotations.SetterTarget, scope.TargetType);
        const string name = AvaloniaStyleMetadata.PropertyMember;
        var attribute = target.Syntax.Attributes.FirstOrDefault(a => a.Name == name || a.LocalName.EndsWith("." + name, StringComparison.Ordinal));
        var property = target.Syntax.Children.OfType<XamlElementSyntax>().FirstOrDefault(e => e.LocalName.EndsWith("." + name, StringComparison.Ordinal));
        var text = attribute?.Value;
        var span = attribute?.ValueSpan ?? property?.Span ?? target.Syntax.NameSpan;
        var propertyScope = property == null ? target.Scope : target.Scope.Push(property);
        if (attribute != null)
        {
            if (text!.StartsWith("{}", StringComparison.Ordinal)) text = text.Substring(2);
            else if (text.StartsWith("{", StringComparison.Ordinal)) text = null;
        }
        else if (property != null)
            text = property.Children.OfType<XamlTextSyntax>().FirstOrDefault()?.Value;
        if (text == null)
        { context.Report("XG3102", "Setter.Property requires a literal property name.", span); return; }
        var resolved = AvaloniaRegisteredPropertyResolver.Resolve(context, scope.TargetType, text, propertyScope, span);
        if (resolved == null) return;
        if (resolved is RegisteredClassProperty && scope.HasComplexActivator)
        { context.Report("XG3118", "A class setter requires a style without a complex activator.", span); return; }
        target.Annotations.Set(AvaloniaStyleAnnotations.SetterProperty, resolved);
    }

    private static bool HasComplexActivator(ObjectBindingBuilder target)
    {
        if (target.Annotations.TryGet(AvaloniaStyleAnnotations.Selector, out var selector))
            return selector.Expression is not BoundCallExpression { Method.Name: "OfType" or "Is" };
        return target.Syntax.Attributes.Any(attribute => attribute.Name == "Selector" || attribute.LocalName.EndsWith(".Selector", StringComparison.Ordinal)) ||
            target.Syntax.Children.OfType<XamlElementSyntax>().Any(element => element.LocalName.EndsWith(".Selector", StringComparison.Ordinal));
    }
}
