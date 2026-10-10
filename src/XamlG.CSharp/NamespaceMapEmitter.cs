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

    private readonly bool _hasNamespaceServices;

    public NamespaceMapEmitter(EmissionContext context)
    {
        _context = context;
        _hasNamespaceServices = context.Document.Runtime.Services.Any(service => service.Mapping.Kind == XamlServiceKind.XmlNamespaces);
    }

    public string GetMap(NamespaceScope scope)
    {
        if (!_hasNamespaceServices) return "null";
        if (_scopeMaps.TryGetValue(scope, out var cached)) return cached;
        var key = _context.NamespacePlan.GetScope(scope).Key;
        if (_maps.TryGetValue(key, out var existing)) return _scopeMaps[scope] = existing.Name;
        var name = "__namespaces_" + _context.Id + "_" + _maps.Count;
        _maps.Add(key, (name, scope));
        return _scopeMaps[scope] = name;
    }

    internal static string ScopeKey(NamespaceScope scope) => NamespaceEmissionPlan.ScopeLayout.Create(scope).Key;

    public void Emit()
    {
        var writer = _context.Writer;
        foreach (var pair in _maps)
        {
            var entry = pair.Value;
            if (_context.SharedServices is { } shared && shared.NamespaceFactories.TryGetValue(pair.Key, out var factory))
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
        var plan = _context.NamespacePlan;
        var aliases = plan.GetScope(scope).Bindings;
        foreach (var contractPlan in plan.Contracts)
        {
            var contract = contractPlan.Contract;
            var item = contractPlan.ItemType;
            var dictionary = "__map" + index++;
            var listType = "global::System.Collections.Generic.IReadOnlyList<" + item + ">";
            writer.Line("var " + dictionary + " = new global::System.Collections.Generic.Dictionary<string, " + listType + ">(global::System.StringComparer.Ordinal);");
            foreach (var alias in aliases)
            {
                var values = contractPlan.GetArrayExpression(alias.Value);
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
