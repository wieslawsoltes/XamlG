using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Tooling;

/// <summary>Symbol-based language operations usable from LSP, workspaces and the browser without a generator driver.</summary>
public sealed class XamlLanguageService
{
    private readonly XamlCompilationSession _compilation;
    public XamlLanguageService(XamlCompilationSession compilation) => _compilation = compilation;

    public XamlHover? GetHover(XamlAnalysis analysis, int position, CancellationToken cancellationToken = default)
    {
        var occurrence = SymbolAt(analysis, position);
        return occurrence == null ? null : new(occurrence.Span, occurrence.Symbol.ToDisplayString(),
            occurrence.Symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken) ?? string.Empty);
    }

    public ImmutableArray<XamlDefinition> GetDefinitions(XamlAnalysis analysis, int position)
    {
        var symbol = SymbolAt(analysis, position)?.Symbol;
        if (symbol == null) return ImmutableArray<XamlDefinition>.Empty;
        return symbol.Locations.Where(l => l.IsInSource).Select(location =>
        {
            var lines = location.GetLineSpan();
            return new XamlDefinition(lines.Path, new(location.SourceSpan.Start, location.SourceSpan.Length),
                new(lines.StartLinePosition.Line, lines.StartLinePosition.Character),
                new(lines.EndLinePosition.Line, lines.EndLinePosition.Character));
        }).ToImmutableArray();
    }

    public ImmutableArray<TextSpan> GetReferences(XamlAnalysis analysis, int position)
    {
        var symbol = SymbolAt(analysis, position)?.Symbol;
        return symbol == null ? ImmutableArray<TextSpan>.Empty : analysis.Document.Symbols
            .Where(s => SymbolEqualityComparer.Default.Equals(s.Symbol, symbol)).Select(s => s.Span).Distinct().ToImmutableArray();
    }

    public ImmutableArray<XamlCompletionItem> GetCompletions(XamlAnalysis analysis, int position,
        XamlCompletionKind kind, CancellationToken cancellationToken = default)
    {
        var element = analysis.Syntax.FindElement(position) ?? analysis.Syntax.Root;
        if (element == null) return ImmutableArray<XamlCompletionItem>.Empty;
        var scope = NamespaceScope.Empty;
        foreach (var ancestor in analysis.Syntax.Root!.DescendantsAndSelf().Where(n => n.Span.Contains(element.Span)).OrderByDescending(n => n.Span.Length))
            scope = scope.Push(ancestor);
        var result = new List<XamlCompletionItem>();
        if (kind == XamlCompletionKind.Element)
        {
            foreach (var mapping in scope.Bindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var type in EnumerateTypes(mapping.Value))
                {
                    var name = mapping.Key.Length == 0 ? type.Name : mapping.Key + ":" + type.Name;
                    result.Add(new(name, name, "Class", type.ToDisplayString()));
                }
            }
        }
        else
        {
            var expanded = scope.Expand(element.Name);
            var type = expanded.Namespace == null ? null : _compilation.Types.Resolve(expanded.Namespace, expanded.LocalName).Type;
            type ??= analysis.Document.Symbols.Where(s => s.Span == element.NameSpan).Select(s => s.Symbol as INamedTypeSymbol).FirstOrDefault(s => s != null);
            if (type != null && kind == XamlCompletionKind.Attribute)
                foreach (var member in type.Members().Where(m => !m.IsStatic && _compilation.Types.IsAccessible(m)))
                {
                    if (element.Attributes.Any(a => a.Name == member.Name)) continue;
                    if (member is IPropertySymbol { IsIndexer: false } || member is IEventSymbol)
                        result.Add(new(member.Name, member.Name + "=\"\"", member is IEventSymbol ? "Event" : "Property", member.ToDisplayString()));
                }
            if (type != null && kind == XamlCompletionKind.Value)
            {
                var attribute = element.Attributes.FirstOrDefault(a => a.ValueSpan.Start <= position && position <= a.ValueSpan.End);
                var property = attribute == null ? null : type.Members(attribute.Name).OfType<IPropertySymbol>().FirstOrDefault();
                if (property?.Type.SpecialType == SpecialType.System_Boolean)
                { result.Add(new("True", "True", "Value", "Boolean")); result.Add(new("False", "False", "Value", "Boolean")); }
                if (property?.Type.TypeKind == TypeKind.Enum)
                    foreach (var field in property.Type.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
                        result.Add(new(field.Name, field.Name, "EnumMember", field.ToDisplayString()));
            }
        }
        return result.GroupBy(c => c.Label, StringComparer.Ordinal).Select(g => g.First()).OrderBy(c => c.Label, StringComparer.Ordinal).ToImmutableArray();
    }

    private IEnumerable<INamedTypeSymbol> EnumerateTypes(string xmlNamespace)
    {
        foreach (var type in _compilation.Types.EnumerateTypes(xmlNamespace)) yield return type;
        string? clr = null; string? assembly = null;
        if (xmlNamespace.StartsWith("using:", StringComparison.Ordinal)) clr = xmlNamespace.Substring(6);
        else if (xmlNamespace.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            var parts = xmlNamespace.Substring(14).Split(';'); clr = parts[0];
            assembly = parts.Skip(1).FirstOrDefault(p => p.StartsWith("assembly=", StringComparison.Ordinal))?.Substring(9) ?? _compilation.Types.Compilation.AssemblyName;
        }
        if (clr == null) yield break;
        foreach (var reference in ImmutableArray.Create(_compilation.Types.Compilation.Assembly).AddRange(_compilation.Types.Compilation.SourceModule.ReferencedAssemblySymbols))
        {
            if (assembly != null && reference.Name != assembly) continue;
            INamespaceSymbol? scope = reference.GlobalNamespace;
            foreach (var part in clr.Split('.')) if (part.Length > 0) scope = scope?.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
            if (scope != null) foreach (var type in scope.GetTypeMembers()) if (_compilation.Types.IsAccessible(type)) yield return type;
        }
    }

    private static XamlG.Compiler.BoundSymbolInfo? SymbolAt(XamlAnalysis analysis, int position) => analysis.Document.Symbols
        .Where(s => s.Span.Contains(position) || s.Span.Length == 0 && s.Span.Start == position)
        .OrderBy(s => s.Span.Length).ThenBy(s => s.Symbol is IMethodSymbol ? 1 : 0).FirstOrDefault();
}
