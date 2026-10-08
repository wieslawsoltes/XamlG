using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal static class CompiledBindingInputReader
{
    private static readonly ImmutableHashSet<string> CompileTimeMembers = ImmutableHashSet.Create(
        StringComparer.Ordinal, AvaloniaBindingMetadata.PathMember, AvaloniaBindingMetadata.DataType,
        AvaloniaBindingMetadata.ElementName, AvaloniaBindingMetadata.RelativeSource);

    public static CompiledBindingInput? FromMarkup(BindingContext context, MarkupExtensionSyntax syntax,
        NamespaceScope scope)
    {
        var positional = syntax.Arguments.Where(a => a.Name == null).ToArray();
        var named = syntax.Arguments.Where(a => a.Name != null).Select(argument =>
        {
            var expanded = scope.Expand(argument.Name!, true);
            return expanded.Namespace != null && XamlNames.IsLanguage(expanded.Namespace) &&
                   expanded.LocalName == AvaloniaBindingMetadata.DataType
                ? argument with { Name = AvaloniaBindingMetadata.DataType } : argument;
        }).ToArray();
        if (named.GroupBy(a => a.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
        {
            context.Report("XG3210", "A binding argument cannot be specified more than once.", syntax.Span);
            return null;
        }
        var namedPath = named.FirstOrDefault(a => a.Name == AvaloniaBindingMetadata.PathMember);
        if (positional.Length > 1 || positional.Length != 0 && namedPath != null)
        {
            context.Report("XG3210", "A binding path may be specified only once.", syntax.Span);
            return null;
        }
        var path = namedPath ?? positional.FirstOrDefault();
        BindingTextValue? Read(string name) => named.FirstOrDefault(a => a.Name == name) is { } argument
            ? new(argument.Value, argument.Span) : null;
        var relativeText = Read(AvaloniaBindingMetadata.RelativeSource);
        var relative = relativeText == null ? null : MarkupExtensionParser.Parse(
            relativeText.Text, relativeText.Span, context.Diagnostics.Add);
        if (relativeText != null && relative == null)
        {
            context.Report("XG3211", "RelativeSource requires a statically configured source extension.", relativeText.Span);
            return null;
        }
        var attributes = named.Where(a => !CompileTimeMembers.Contains(a.Name!))
            .Select(a => new XamlAttributeSyntax(a.Name!, a.Value, a.Span, a.Span, a.Span, '"')).ToImmutableArray();
        var element = new XamlElementSyntax(syntax.Name, syntax.Span, syntax.Span,
            new(syntax.Span.End, 0), attributes, ImmutableArray<XamlSyntaxNode>.Empty, true, syntax.Span);
        return new(element, scope, scope, new(path?.Value ?? string.Empty, path?.Span ?? syntax.Span),
            Read(AvaloniaBindingMetadata.DataType), Read(AvaloniaBindingMetadata.ElementName), relative == null ? null : new(relative, scope));
    }

    public static CompiledBindingInput? FromElement(BindingContext context, XamlElementSyntax syntax,
        NamespaceScope parentScope, INamedTypeSymbol compiledType)
    {
        var scope = parentScope.Push(syntax);
        var values = new Dictionary<string, BindingTextValue>(StringComparer.Ordinal);
        var failed = false;
        BindingRelativeSourceInput? relative = null;
        void Add(string name, string text, TextSpan span, NamespaceScope? valueScope = null)
        {
            if (values.ContainsKey(name))
            {
                context.Report("XG3210", "Binding member '" + name + "' is assigned more than once.", span);
                failed = true;
            }
            else values.Add(name, new(text, span, valueScope));
        }
        var attributes = ImmutableArray.CreateBuilder<XamlAttributeSyntax>();
        foreach (var attribute in syntax.Attributes)
        {
            var expanded = scope.Expand(attribute.Name, true);
            var isDataType = expanded.Namespace != null && XamlNames.IsLanguage(expanded.Namespace) &&
                             expanded.LocalName == AvaloniaBindingMetadata.DataType;
            if (CompileTimeMembers.Contains(attribute.Name) || isDataType)
                Add(isDataType ? AvaloniaBindingMetadata.DataType : attribute.Name, attribute.Value, attribute.ValueSpan);
            else attributes.Add(attribute);
        }
        var children = ImmutableArray.CreateBuilder<XamlSyntaxNode>();
        foreach (var child in syntax.Children)
        {
            if (child is not XamlElementSyntax propertyElement || !propertyElement.LocalName.Contains('.'))
            {
                if (child is XamlTextSyntax text && !string.IsNullOrWhiteSpace(text.Value))
                {
                    context.Report("XG3210", "A binding object accepts properties, not implicit text content.", text.Span);
                    failed = true;
                }
                else children.Add(child);
                continue;
            }
            var name = propertyElement.LocalName.Substring(propertyElement.LocalName.LastIndexOf('.') + 1);
            if (CompileTimeMembers.Contains(name))
            {
                var objects = propertyElement.Children.OfType<XamlElementSyntax>().ToArray();
                if (name == AvaloniaBindingMetadata.RelativeSource && objects.Length == 1)
                {
                    Add(name, string.Empty, propertyElement.Span);
                    var source = objects[0];
                    var sourceScope = scope.Push(propertyElement).Push(source);
                    var sourceType = context.ResolveType(source.Name, sourceScope, source.NameSpan);
                    if (sourceType == null || !(sourceType.HasMetadataName(AvaloniaBindingMetadata.RelativeSourceType) ||
                                               sourceType.HasMetadataName(AvaloniaBindingMetadata.RelativeSourceExtension)))
                    {
                        context.Report("XG3211", "The source object must be a RelativeSource.", source.NameSpan);
                        failed = true;
                    }
                    else
                    {
                        relative = new(new(source.Name, source.Attributes.Where(a => !a.IsNamespace)
                            .Select(a => new MarkupArgumentSyntax(a.Name, a.Value, a.ValueSpan)).ToImmutableArray(), source.Span), sourceScope);
                    }
                }
                else if (objects.Length == 0)
                {
                    var textNodes = propertyElement.Children.OfType<XamlTextSyntax>().ToArray();
                    var text = string.Concat(textNodes.Select(t => t.Value)).Trim();
                    Add(name, text, textNodes.Length == 1 ? textNodes[0].Span : propertyElement.Span, scope.Push(propertyElement));
                }
                else
                {
                    context.Report("XG3210", "Binding metadata '" + name + "' requires a static text value.", propertyElement.Span);
                    failed = true;
                }
            }
            else
            {
                // The CLR owner changes from Binding to CompiledBindingExtension. Keep the original
                // property member and its exact source span, but do not resolve the old owner type.
                var prefixEnd = propertyElement.Name.IndexOf(':');
                var prefix = prefixEnd < 0 ? string.Empty : propertyElement.Name.Substring(0, prefixEnd + 1);
                children.Add(propertyElement with { Name = prefix + compiledType.Name + "." + name });
            }
        }
        BindingTextValue? Read(string name) => values.TryGetValue(name, out var value) ? value : null;
        var relativeText = Read(AvaloniaBindingMetadata.RelativeSource);
        if (relative == null && relativeText != null)
        {
            var parsed = MarkupExtensionParser.Parse(relativeText.Text, relativeText.Span, context.Diagnostics.Add);
            if (parsed != null) relative = new(parsed, relativeText.Scope ?? scope);
        }
        if (relativeText != null && relative == null)
        {
            context.Report("XG3211", "RelativeSource requires a statically configured source.", relativeText.Span);
            failed = true;
        }
        if (failed) return null;
        var normalized = syntax with { Attributes = attributes.ToImmutable(), Children = children.ToImmutable() };
        return new(normalized, parentScope, parentScope.Push(normalized),
            Read(AvaloniaBindingMetadata.PathMember) ?? new(string.Empty, syntax.NameSpan),
            Read(AvaloniaBindingMetadata.DataType), Read(AvaloniaBindingMetadata.ElementName), relative);
    }
}
