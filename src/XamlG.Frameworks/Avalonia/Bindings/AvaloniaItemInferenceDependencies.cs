using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

/// <summary>Finds collection properties consumed by descendant item scopes before those values are bound.</summary>
internal sealed class AvaloniaItemInferenceDependencies(BindingContext context, ObjectBindingBuilder owner)
{
    private static readonly XamlAnnotationKey<HashSet<string>> Key = new("Avalonia.ItemInferenceDependencies");
    private static readonly string[] DataTypeAttributes = { AvaloniaBindingMetadata.DataTypeAttribute };
    private readonly HashSet<string> _sources = new(StringComparer.Ordinal);
    private readonly List<ObjectBindingBuilder> _objects = context.Ancestors.Skip(1).Reverse().ToList();

    public static bool Requires(BindingContext context, ObjectBindingBuilder target, BoundMember member)
    {
        if (!target.Annotations.TryGet(Key, out var sources))
        {
            var reader = new AvaloniaItemInferenceDependencies(context, target);
            reader.Visit(target);
            target.Annotations.Set(Key, sources = reader._sources);
        }
        return sources.Contains(member.Name);
    }

    private void Visit(ObjectBindingBuilder target)
    {
        context.Cancellation.ThrowIfCancellationRequested();
        _objects.Add(target);
        foreach (var property in BindingPropertyValue.Read(context, target))
        {
            var expanded = property.Scope.Expand(property.Name, true);
            if (expanded.Namespace is { } ns && XamlNames.IsLanguage(ns)) continue;
            var member = context.Members.Resolve(target.Type, property.Name, property.Scope, property.NameSpan, report: false);
            VisitValues(member, property.Values, property.Scope);
        }
        var content = context.Types.GetContentProperty(target.Type);
        var contentMember = content == null ? null : context.Members.Resolve(target.Type, content, target.Scope, target.Syntax.NameSpan, isImplicitContent: true);
        VisitValues(contentMember, target.Syntax.Children.Where(node => node is not XamlElementSyntax element || !element.LocalName.Contains('.')), target.Scope);
        _objects.RemoveAt(_objects.Count - 1);
    }

    private void VisitValues(BoundMember? member, IEnumerable<XamlSyntaxNode> values, NamespaceScope scope)
    {
        var dependency = member == null ? null : AvaloniaItemTypeInference.Dependency(member);
        foreach (var value in values)
        {
            var target = Constructable(value, scope);
            if (target == null) continue;
            if (dependency is { } item && !HasOwnType(target))
            {
                var sourceOwner = item.Ancestor == null ? _objects.Last() : _objects.LastOrDefault(candidate =>
                    context.Types.Compilation.ClassifyCommonConversion(candidate.Type, item.Ancestor).IsImplicit);
                if (ReferenceEquals(sourceOwner, owner)) _sources.Add(item.Source);
            }
            Visit(target);
        }
    }

    private ObjectBindingBuilder? Constructable(XamlSyntaxNode value, NamespaceScope scope)
    {
        XamlElementSyntax syntax;
        INamedTypeSymbol? type;
        if (value is XamlTextSyntax text)
        {
            if (!text.Value.StartsWith("{", StringComparison.Ordinal) || text.Value.StartsWith("{}", StringComparison.Ordinal)) return null;
            var markup = MarkupExtensionParser.Parse(text.Value, text.Span, _ => { });
            if (markup == null || IsIntrinsic(scope, markup.Name)) return null;
            type = context.ResolveType(markup.Name, scope, markup.Span, report: false, extension: true);
            syntax = new(markup.Name, markup.Span, markup.Span, new(markup.Span.End, 0),
                markup.Arguments.Where(argument => argument.Name != null).Select(argument =>
                    new XamlAttributeSyntax(argument.Name!, argument.Value, argument.Span, argument.Span, argument.Span, '"')).ToImmutableArray(),
                ImmutableArray<XamlSyntaxNode>.Empty, true, markup.Span);
        }
        else if (value is XamlElementSyntax element)
        {
            syntax = element;
            scope = scope.Push(element);
            var ns = scope.Expand(element.Name).Namespace;
            if (IsIntrinsic(scope, element.Name) || ns != null && (scope.IgnoredNamespaces.Contains(ns) || context.Types.Configuration.IgnoredNamespaces.Contains(ns))) return null;
            type = context.ResolveType(element.Name, scope, element.NameSpan, scope.Directive(element, "TypeArguments")?.Value, report: false);
        }
        else return null;
        return type == null ? null : new(type, syntax, scope, string.Empty, false, owner.NameScopeId);
    }

    private bool HasOwnType(ObjectBindingBuilder target)
    {
        var values = BindingPropertyValue.Read(context, target).ToArray();
        var directive = target.Scope.Directive(target.Syntax, AvaloniaBindingMetadata.DataType);
        if (directive != null)
        {
            var mapped = !values.Any(value => value.MemberName == AvaloniaBindingMetadata.DataType) &&
                target.Type.Members(AvaloniaBindingMetadata.DataType).OfType<IPropertySymbol>().Any(property => property.HasAttribute(DataTypeAttributes));
            if (!mapped || IsTypeValue(new XamlTextSyntax(directive.Value, false, directive.ValueSpan), target.Scope)) return true;
        }
        foreach (var value in values)
        {
            var member = context.Members.Resolve(target.Type, value.Name, value.Scope, value.NameSpan, report: false);
            if (member?.Symbol is not IPropertySymbol property) continue;
            var nodes = value.Values.Where(node => node is XamlElementSyntax || node is XamlTextSyntax text && !string.IsNullOrWhiteSpace(text.Value)).ToArray();
            if (nodes.Length != 1) continue;
            if (property.HasAttribute(DataTypeAttributes) && IsTypeValue(nodes[0], value.Scope)) return true;
            if (property.Name == AvaloniaBindingMetadata.DataContext && property.ContainingType.HasMetadataName(AvaloniaStyleMetadata.StyledElement) &&
                Constructable(nodes[0], value.Scope) is { } dataContext &&
                (nodes[0] is XamlTextSyntax || context.Types.MarkupExtensionMethod(dataContext.Type) != null)) return true;
        }
        return false;
    }

    private static bool IsIntrinsic(NamespaceScope scope, string name) =>
        scope.Expand(name).Namespace is { } ns && XamlNames.IsLanguage(ns);

    private static bool IsTypeValue(XamlSyntaxNode value, NamespaceScope scope)
    {
        if (value is XamlElementSyntax element)
            return scope.Push(element).Expand(element.Name) is { LocalName: "Type", Namespace: { } ns } && XamlNames.IsLanguage(ns);
        if (value is not XamlTextSyntax text) return false;
        var input = text.Value.Trim();
        if (!input.StartsWith("{", StringComparison.Ordinal)) return input.Length != 0;
        var markup = MarkupExtensionParser.Parse(input, text.Span, _ => { });
        return markup != null && scope.Expand(markup.Name) is { LocalName: "Type", Namespace: { } xmlns } && XamlNames.IsLanguage(xmlns);
    }
}
