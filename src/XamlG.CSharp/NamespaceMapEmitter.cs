using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.CSharp;

/// <summary>Emits immutable-per-document namespace dictionaries once, shared by all construction frames.</summary>
internal sealed class NamespaceMapEmitter
{
    private readonly EmissionContext _context;
    private readonly Dictionary<string, (string Name, NamespaceScope Scope)> _maps = new(StringComparer.Ordinal);

    public NamespaceMapEmitter(EmissionContext context) => _context = context;

    public string GetMap(NamespaceScope scope)
    {
        if (!_context.Document.Runtime.Services.Any(s => s.Mapping.Kind == XamlServiceKind.XmlNamespaces)) return "null";
        var key = string.Join("\n", scope.Bindings.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));
        if (_maps.TryGetValue(key, out var existing)) return existing.Name;
        var name = "__namespaces_" + _context.Id + "_" + _maps.Count;
        _maps.Add(key, (name, scope));
        return name;
    }

    public void Emit()
    {
        var writer = _context.Writer;
        foreach (var entry in _maps.Values)
        {
            writer.Line("private static readonly global::System.Collections.Generic.IReadOnlyDictionary<global::System.Type, object> " + entry.Name + " = " + entry.Name + "_Create();");
            writer.Open("private static global::System.Collections.Generic.IReadOnlyDictionary<global::System.Type, object> " + entry.Name + "_Create()");
            writer.Line("var __services = new global::System.Collections.Generic.Dictionary<global::System.Type, object>();");
            var index = 0;
            foreach (var contract in _context.Document.Runtime.Services.Where(s => s.Mapping.Kind == XamlServiceKind.XmlNamespaces))
            {
                if (contract.NamespaceItemType == null || contract.NamespaceNameProperty == null || contract.AssemblyNameProperty == null) continue;
                var item = contract.NamespaceItemType.CSharpName();
                var dictionary = "__map" + index++;
                var listType = "global::System.Collections.Generic.IReadOnlyList<" + item + ">";
                writer.Line("var " + dictionary + " = new global::System.Collections.Generic.Dictionary<string, " + listType + ">(global::System.StringComparer.Ordinal);");
                foreach (var alias in entry.Scope.Bindings.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    var mappings = _context.Document.Runtime.NamespaceMappings.Where(m => m.XmlNamespace == alias.Value).ToArray();
                    var items = mappings.Select(m => "new " + item + " { " +
                        CSharpNames.Identifier(contract.NamespaceNameProperty.Name) + " = " + CSharpNames.Literal(m.ClrNamespace) + ", " +
                        CSharpNames.Identifier(contract.AssemblyNameProperty.Name) + " = " + CSharpNames.Literal(m.AssemblyName ?? _context.Document.Runtime.LocalAssemblyName) + " }");
                    writer.Line(dictionary + ".Add(" + CSharpNames.Literal(alias.Key) + ", global::System.Array.AsReadOnly(new " + item + "[] { " + string.Join(", ", items) + " }));");
                }
                writer.Line("__services.Add(typeof(" + contract.InterfaceType.CSharpName() + "), new global::System.Collections.ObjectModel.ReadOnlyDictionary<string, " + listType + ">(" + dictionary + "));");
            }
            writer.Line("return new global::System.Collections.ObjectModel.ReadOnlyDictionary<global::System.Type, object>(__services);");
            writer.Close();
        }
    }
}
