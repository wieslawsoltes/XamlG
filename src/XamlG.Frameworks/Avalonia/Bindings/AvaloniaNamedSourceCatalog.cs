using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.References;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

/// <summary>Indexes object names and deferred-content boundaries without binding object values.</summary>
internal sealed class AvaloniaNamedSourceCatalog
{
    internal sealed record Entry(INamedTypeSymbol Type, XamlElementSyntax Syntax, NamespaceScope Scope,
        Entry? Parent, BoundMember? Member, XamlElementSyntax ScopeRoot, int Order)
    {
        public ObjectBindingBuilder Builder(int nameScope) => new(Type, Syntax, Scope, string.Empty, Parent == null, nameScope);
    }

    private readonly BindingContext _context;
    private readonly Dictionary<XamlElementSyntax, Dictionary<string, Entry>> _names = new();
    private readonly Dictionary<XamlElementSyntax, Entry> _entries = new();
    private readonly XamlElementSyntax _root;

    public AvaloniaNamedSourceCatalog(BindingContext context, ObjectBindingBuilder root)
    {
        _context = context; _root = root.Syntax;
        Visit(new(root.Type, root.Syntax, root.Scope, null, null, root.Syntax, 0));
    }

    public int Order(XamlElementSyntax syntax) => _entries.TryGetValue(syntax, out var entry) ? entry.Order :
        _entries.Values.Where(candidate => candidate.Syntax.Span.Contains(syntax.Span)).OrderBy(candidate => candidate.Syntax.Span.Length).FirstOrDefault()?.Order + 1 ?? int.MaxValue;

    public Entry? Find(string name)
    {
        // The pinned compiler searches the document scope before enclosing template scopes.
        if (_names.TryGetValue(_root, out var rootNames) && rootNames.TryGetValue(name, out var root)) return root;
        var searched = new HashSet<XamlElementSyntax> { _root };
        foreach (var ancestor in _context.Ancestors)
            if (_entries.TryGetValue(ancestor.Syntax, out var entry) && searched.Add(entry.ScopeRoot) &&
                _names.TryGetValue(entry.ScopeRoot, out var names) && names.TryGetValue(name, out var found)) return found;
        return null;
    }

    public int NameScope(Entry entry)
    {
        foreach (var ancestor in _context.Ancestors)
            if (_entries.TryGetValue(ancestor.Syntax, out var candidate) && candidate.ScopeRoot == entry.ScopeRoot)
                return ancestor.NameScopeId;
        return 0;
    }

    private void Visit(Entry entry)
    {
        _context.Cancellation.ThrowIfCancellationRequested();
        _entries.Add(entry.Syntax, entry);
        var target = entry.Builder(0);
        foreach (var name in AvaloniaLiteralName.Read(_context, target))
        {
            if (!_names.TryGetValue(entry.ScopeRoot, out var names)) _names.Add(entry.ScopeRoot, names = new(StringComparer.Ordinal));
            if (!names.ContainsKey(name.Value)) names.Add(name.Value, entry);
        }
        foreach (var property in BindingPropertyValue.Read(_context, target))
        {
            if (!property.Values.Any(value => value is XamlElementSyntax)) continue;
            var member = _context.Members.Resolve(entry.Type, property.Name, property.Scope, property.NameSpan, report: false);
            VisitValues(entry, member, property.Values, property.Scope);
        }
        var content = _context.Types.GetContentProperty(entry.Type);
        var contentMember = content == null ? null : _context.Members.Resolve(entry.Type, content, entry.Scope, entry.Syntax.NameSpan, isImplicitContent: true);
        VisitValues(entry, contentMember, entry.Syntax.Children.Where(node => node is not XamlElementSyntax element || !element.LocalName.Contains('.')), entry.Scope);
    }

    private void VisitValues(Entry parent, BoundMember? member, IEnumerable<XamlSyntaxNode> values, NamespaceScope scope)
    {
        foreach (var syntax in values.OfType<XamlElementSyntax>())
        {
            var nested = scope.Push(syntax);
            var ns = nested.Expand(syntax.Name).Namespace;
            if (ns == null || nested.IgnoredNamespaces.Contains(ns) || _context.Types.Configuration.IgnoredNamespaces.Contains(ns)) continue;
            if (XamlNames.IsLanguage(ns))
            {
                VisitValues(parent, member, syntax.Children, nested);
                continue;
            }
            var type = _context.ResolveType(syntax.Name, nested, syntax.NameSpan, nested.Directive(syntax, "TypeArguments")?.Value, report: false);
            if (type == null) continue;
            var scopeRoot = member?.Symbol.HasAttribute(_context.Types.Configuration.DeferredContentAttributes) == true ? syntax : parent.ScopeRoot;
            Visit(new(type, syntax, nested, parent, member, scopeRoot, _entries.Count));
        }
    }
}
