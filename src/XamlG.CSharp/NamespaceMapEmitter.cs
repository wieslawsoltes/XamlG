using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.CSharp;

/// <summary>Emits immutable-per-document namespace dictionaries once, shared by all construction frames.</summary>
internal sealed class NamespaceMapEmitter
{
    private const string MapType = "global::System.Collections.Generic.IReadOnlyDictionary<global::System.Type, object>";
    private readonly EmissionContext _context;
    private readonly Dictionary<string, (string Name, NamespaceScope Scope)> _maps = new(StringComparer.Ordinal);
    private readonly Dictionary<NamespaceScope, string> _scopeMaps = new();

    public NamespaceMapEmitter(EmissionContext context) => _context = context;

    public string GetMap(NamespaceScope scope)
    {
        if (!_context.Document.Runtime.Services.Any(s => s.Mapping.Kind == XamlServiceKind.XmlNamespaces)) return "null";
        if (_scopeMaps.TryGetValue(scope, out var cached)) return cached;
        var key = ScopeKey(scope);
        if (_maps.TryGetValue(key, out var existing)) return _scopeMaps[scope] = existing.Name;
        var name = "__namespaces_" + _context.Id + "_" + _maps.Count;
        _maps.Add(key, (name, scope));
        return _scopeMaps[scope] = name;
    }

    internal static string ScopeKey(NamespaceScope scope) => string.Join("\n", scope.Bindings
        .Where(p => scope.DeclaredPrefixes.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

    public void Emit()
    {
        var writer = _context.Writer;
        foreach (var entry in _maps.Values)
        {
            if (_context.SharedServices is { } shared && shared.NamespaceFactories.TryGetValue(ScopeKey(entry.Scope), out var factory))
                writer.Line("private static readonly " + MapType + " " + entry.Name + " = " + shared.TypeName + "." + factory + "();");
            else
            {
                writer.Line("private static readonly " + MapType + " " + entry.Name + " = " + entry.Name + "_Create();");
                EmitFactory(writer, entry.Scope, entry.Name + "_Create", "private");
            }
        }
    }

    internal void EmitFactory(CSharpWriter writer, NamespaceScope scope, string name, string accessibility)
    {
        writer.Open(accessibility + " static " + MapType + " " + name + "()");
        writer.Line("var __services = new global::System.Collections.Generic.Dictionary<global::System.Type, object>();");
        var index = 0;
        foreach (var contract in _context.Document.Runtime.Services.Where(s => s.Mapping.Kind == XamlServiceKind.XmlNamespaces))
        {
            if (contract.NamespaceItemType == null || contract.NamespaceNameProperty == null || contract.AssemblyNameProperty == null) continue;
            var item = contract.NamespaceItemType.CSharpName();
            var dictionary = "__map" + index++;
            var listType = "global::System.Collections.Generic.IReadOnlyList<" + item + ">";
            writer.Line("var " + dictionary + " = new global::System.Collections.Generic.Dictionary<string, " + listType + ">(global::System.StringComparer.Ordinal);");
            foreach (var alias in scope.Bindings.Where(p => scope.DeclaredPrefixes.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var mappings = _context.Document.Runtime.NamespaceMappings.Where(m => m.XmlNamespace == alias.Value).ToArray();
                var items = mappings.Select(m => "new " + item + " { " +
                    CSharpNames.Identifier(contract.NamespaceNameProperty.Name) + " = " + CSharpNames.Literal(m.ClrNamespace) + ", " +
                    CSharpNames.Identifier(contract.AssemblyNameProperty.Name) + " = " + (m.AssemblyName == null ? "null" : CSharpNames.Literal(m.AssemblyName)) + " }");
                var values = "new " + item + "[] { " + string.Join(", ", items) + " }";
                if (_context.Document.Profile.Runtime.ProtectNamespaceDictionaries)
                    values = "global::System.Array.AsReadOnly(" + values + ")";
                writer.Line(dictionary + ".Add(" + CSharpNames.Literal(alias.Key) + ", " + values + ");");
            }
            var exposed = _context.Document.Profile.Runtime.ProtectNamespaceDictionaries
                ? "new global::System.Collections.ObjectModel.ReadOnlyDictionary<string, " + listType + ">(" + dictionary + ")"
                : dictionary;
            writer.Line("__services.Add(typeof(" + contract.InterfaceType.CSharpName() + "), " + exposed + ");");
        }
        writer.Line("return new global::System.Collections.ObjectModel.ReadOnlyDictionary<global::System.Type, object>(__services);");
        writer.Close();
    }
}
