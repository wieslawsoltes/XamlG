using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Roslyn;
using XamlG.Syntax;
namespace XamlG.Compiler;

public sealed class ObjectBinder
{
    private readonly BindingContext _context;
    private readonly ConstructorBinder _constructors;
    public ObjectBinder(BindingContext context) { _context = context; _constructors = new(context); }
    public BoundObject? Bind(XamlElementSyntax syntax, NamespaceScope parentScope, INamedTypeSymbol? overrideType = null,
        bool isRoot = false, int nameScope = 0, ImmutableArray<XamlSyntaxNode> positional = default)
    {
        _context.Cancellation.ThrowIfCancellationRequested();
        var scope = parentScope.Push(syntax);
        var type = overrideType ?? _context.ResolveType(syntax.Name, scope, syntax.NameSpan, scope.Directive(syntax, "TypeArguments")?.Value);
        if (type == null) return null;
        if (type.IsAbstract && !isRoot) { _context.Report("XG1006", $"Cannot instantiate abstract type '{type}'.", syntax.NameSpan); return null; }
        if (_context.Types.GetDeclaredContentProperties(type).Length > 1)
            _context.Report("XG1026", $"Type '{type}' declares more than one content property.", syntax.NameSpan);
        var nameAttribute = scope.Directive(syntax, "Name");
        var key = nameAttribute == null ? _context.NewObjectKey() : "s" + nameScope + ":" + nameAttribute.Value;
        var builder = new ObjectBindingBuilder(type, syntax, scope, key, isRoot, nameScope);
        if (nameAttribute != null)
        {
            if (!SyntaxFacts.IsValidIdentifier(nameAttribute.Value)) _context.Report("XG1012", $"Invalid XAML name '{nameAttribute.Value}'.", nameAttribute.ValueSpan);
            else { builder.Name = nameAttribute.Value; _context.RegisterName(nameScope, builder.Name, type, nameAttribute.ValueSpan); }
        }
        var modifier = scope.Directive(syntax, "FieldModifier");
        if (modifier != null)
        {
            if (modifier.Value is "public" or "internal" or "private" or "protected" or "protected internal" or "private protected") builder.FieldModifier = modifier.Value;
            else _context.Report("XG1018", "Invalid x:FieldModifier.", modifier.ValueSpan);
        }
        _context.Ancestors.Push(builder);
        try
        {
            foreach (var rule in _context.Profile.ObjectBindingRules) rule.Initialize(_context, builder);
            var argumentsElement = syntax.Children.OfType<XamlElementSyntax>().FirstOrDefault(e =>
            { var n = scope.Push(e).Expand(e.Name); return n.Namespace != null && XamlNames.IsLanguage(n.Namespace) && n.LocalName == "Arguments"; });
            var args = positional.IsDefault ? argumentsElement?.Children.Where(n => n is XamlElementSyntax || n is XamlTextSyntax t && !string.IsNullOrWhiteSpace(t.Value)).ToImmutableArray() ?? ImmutableArray<XamlSyntaxNode>.Empty : positional;
            _constructors.Bind(builder, args);
            foreach (var attribute in syntax.Attributes)
            {
                if (attribute.IsNamespace) continue;
                var expanded = scope.Expand(attribute.Name, true);
                if (expanded.Namespace == XamlNames.Xml || expanded.Namespace == XamlNames.Compatibility || expanded.Namespace != null &&
                    (scope.IgnoredNamespaces.Contains(expanded.Namespace) || _context.Types.Configuration.IgnoredNamespaces.Contains(expanded.Namespace))) continue;
                var handled = false;
                foreach (var rule in _context.Profile.BindingRules)
                    if (rule.TryBindAttribute(_context, builder, attribute, scope)) { handled = true; break; }
                if (handled) continue;
                if (expanded.Namespace != null && XamlNames.IsLanguage(expanded.Namespace))
                {
                    switch (expanded.LocalName)
                    {
                        case "Class": case "ClassModifier": case "Name": case "FieldModifier": case "TypeArguments": case "Key": case "FactoryMethod": break;
                        default: _context.Report("XG1020", $"Directive '{attribute.Name}' is not supported by profile '{_context.Profile.Name}'.", attribute.NameSpan); break;
                    }
                    continue;
                }
                if (expanded.Namespace == null) { _context.Report("XG1001", $"Namespace prefix for '{attribute.Name}' is not declared.", attribute.NameSpan); continue; }
                _context.Members.BindAttribute(builder, attribute, scope);
            }
            if (builder.Name != null && nameAttribute != null)
            {
                var nameProperty = _context.Members.Resolve(type, "Name", scope, nameAttribute!.Span, report: false);
                if (nameProperty?.CanWrite == true && !builder.AssignedScalars.Contains(nameProperty.Symbol.ToDisplayString()))
                    _context.Members.AddSet(builder, nameProperty, new BoundConstantExpression(builder.Name, _context.Types.Special(SpecialType.System_String), nameAttribute.Span), nameAttribute.Span);
            }
            var content = new List<XamlSyntaxNode>();
            for (var childIndex = 0; childIndex < syntax.Children.Length; childIndex++)
            {
                var child = syntax.Children[childIndex];
                if (child is XamlTextSyntax { Value: var whitespace } && string.IsNullOrWhiteSpace(whitespace))
                {
                    bool PropertyAt(int index) => index >= 0 && index < syntax.Children.Length &&
                        syntax.Children[index] is XamlElementSyntax adjacent && adjacent.LocalName.IndexOf('.') >= 0;
                    if (PropertyAt(childIndex - 1) || PropertyAt(childIndex + 1)) continue;
                }
                if (ReferenceEquals(child, argumentsElement)) continue;
                if (child is XamlElementSyntax propertyElement)
                {
                    var propertyScope = scope.Push(propertyElement);
                    var expanded = propertyScope.Expand(propertyElement.Name);
                    if (expanded.Namespace != null && (propertyScope.IgnoredNamespaces.Contains(expanded.Namespace) || _context.Types.Configuration.IgnoredNamespaces.Contains(expanded.Namespace))) continue;
                    if (propertyElement.LocalName.IndexOf('.') >= 0)
                    {
                        var member = _context.Members.Resolve(type, propertyElement.Name, propertyScope, propertyElement.NameSpan);
                        if (member != null) _context.Members.BindNodes(builder, member, propertyElement.Children, propertyScope, propertyElement.Span);
                        continue;
                    }
                }
                content.Add(child);
            }
            if (content.Any(n => n is XamlElementSyntax || n is XamlTextSyntax t && (!string.IsNullOrWhiteSpace(t.Value) || scope.PreserveSpace)))
            {
                var contentProperty = _context.Types.GetContentProperty(type);
                var member = contentProperty == null ? null : _context.Members.Resolve(type, contentProperty.Name, scope, syntax.NameSpan);
                _context.Members.BindNodes(builder, member, content, scope, syntax.Span);
            }
            foreach (var rule in _context.Profile.ObjectBindingRules) rule.Complete(_context, builder);
            return builder.Build(_context.Types);
        }
        finally { _context.Ancestors.Pop(); }
    }
}
