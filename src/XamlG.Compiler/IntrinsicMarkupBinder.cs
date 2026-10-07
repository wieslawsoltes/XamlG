using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>Normalizes intrinsic markup and object forms while preserving the namespace
/// scopes of property values and explicit generic arguments independently.</summary>
internal sealed class IntrinsicMarkupBinder(BindingContext context, bool report = true)
{
    public BoundExpression? Bind(MarkupExtensionSyntax syntax, ITypeSymbol target, NamespaceScope scope)
    {
        var arguments = ImmutableArray.CreateBuilder<MarkupArgumentSyntax>();
        string? typeArguments = null;
        foreach (var argument in syntax.Arguments)
        {
            var name = argument.Name == null ? default : scope.Expand(argument.Name, true);
            if (name.Namespace != null && XamlNames.IsLanguage(name.Namespace) && name.LocalName == "TypeArguments")
            {
                if (typeArguments != null) return Invalid("Duplicate x:TypeArguments.", argument.Span);
                typeArguments = argument.Value;
            }
            else arguments.Add(argument);
        }
        return BindCore(scope.Expand(syntax.Name).LocalName, arguments.ToImmutable(), target, scope, syntax.Span, typeArguments, scope);
    }

    public BoundExpression? BindObject(XamlElementSyntax syntax, ITypeSymbol target, NamespaceScope scope)
    {
        var kind = scope.Expand(syntax.Name).LocalName;
        var property = ArgumentProperty(kind);
        var arguments = ImmutableArray.CreateBuilder<MarkupArgumentSyntax>();
        var valueScope = scope;
        string? typeArguments = null;
        foreach (var attribute in syntax.Attributes)
        {
            var name = scope.Expand(attribute.Name, true);
            if (attribute.IsNamespace || name.Namespace == XamlNames.Xml && name.LocalName == "space" || IsIgnored(name, scope)) continue;
            if (name.Namespace != null && XamlNames.IsLanguage(name.Namespace))
            {
                if (name.LocalName == "Key") continue;
                if (name.LocalName == "TypeArguments")
                {
                    if (typeArguments != null) return Invalid("Duplicate x:TypeArguments.", attribute.Span);
                    typeArguments = attribute.Value;
                    continue;
                }
            }
            arguments.Add(new(attribute.Name, attribute.Value, attribute.ValueSpan));
        }
        foreach (var child in syntax.Children)
        {
            if (child is XamlTriviaSyntax || child is XamlTextSyntax text && string.IsNullOrWhiteSpace(text.Value)) continue;
            if (child is not XamlElementSyntax element) return Invalid($"x:{kind} does not accept direct text content.", child.Span);
            var nested = scope.Push(element);
            var name = nested.Expand(element.Name);
            if (IsIgnored(name, nested)) continue;
            if (property == null || name.Namespace == null || !XamlNames.IsLanguage(name.Namespace) || name.LocalName != kind + "." + property)
                return Invalid($"Unsupported property element '{element.Name}' on x:{kind}.", element.NameSpan);
            if (element.Children.OfType<XamlElementSyntax>().Any()) return Invalid($"x:{kind}.{property} requires text.", element.Span);
            foreach (var attribute in element.Attributes)
            {
                var attributeName = nested.Expand(attribute.Name, true);
                if (!attribute.IsNamespace && !(attributeName.Namespace == XamlNames.Xml && attributeName.LocalName == "space") && !IsIgnored(attributeName, nested))
                    return Invalid($"Unsupported attribute '{attribute.Name}' on x:{kind}.{property}.", attribute.NameSpan);
            }
            arguments.Add(new(property, string.Concat(element.Children.OfType<XamlTextSyntax>().Select(text => text.Value)), element.Span));
            valueScope = nested;
        }
        return BindCore(kind, arguments.ToImmutable(), target, valueScope, syntax.Span, typeArguments, scope);
    }

    private BoundExpression? BindCore(string kind, ImmutableArray<MarkupArgumentSyntax> arguments, ITypeSymbol target,
        NamespaceScope scope, TextSpan span, string? typeArguments, NamespaceScope typeArgumentScope)
    {
        var property = ArgumentProperty(kind);
        if (typeArguments != null && kind is not ("Type" or "Static")) return Invalid($"x:{kind} does not accept x:TypeArguments.", span);
        if (kind is "Null" or "True" or "False")
        {
            if (arguments.Length != 0) return Invalid($"x:{kind} does not accept arguments.", span);
            return kind == "Null" ? new BoundConstantExpression(null, null, span) :
                new BoundConstantExpression(kind == "True", context.Types.Special(SpecialType.System_Boolean), span);
        }
        if (property == null) return Invalid($"Unsupported intrinsic markup extension 'x:{kind}'.", span);
        if (arguments.Length != 1 || arguments[0].Name != null && arguments[0].Name != property || string.IsNullOrWhiteSpace(arguments[0].Value))
            return Invalid($"x:{kind} requires exactly one positional argument or {property} property.", span);
        var argument = arguments[0].Value.Trim();
        switch (kind)
        {
            case "Type":
                var type = context.Values.ResolveTypeLiteral(argument, scope, span, typeArguments, report, typeArgumentScope);
                return type == null ? null : new BoundTypeExpression(type, context.Types.Find(ClrNames.Type)!, span);
            case "Static": return new MarkupBinder(context).Static(argument, target, scope, span, typeArguments, typeArgumentScope, report);
            case "Reference": return new BoundReferenceExpression(argument, target, span);
            default: return null;
        }
    }

    private static string? ArgumentProperty(string kind) => kind switch { "Type" => "TypeName", "Static" => "Member", "Reference" => "Name", _ => null };
    private bool IsIgnored(ExpandedName name, NamespaceScope scope) => name.Namespace != null &&
        (name.Namespace == XamlNames.Compatibility || scope.IgnoredNamespaces.Contains(name.Namespace) || context.Types.Configuration.IgnoredNamespaces.Contains(name.Namespace));
    private BoundExpression? Invalid(string message, TextSpan span) { if (report) context.Report("XG1009", message, span); return null; }
}
