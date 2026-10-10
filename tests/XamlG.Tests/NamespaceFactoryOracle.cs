using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Tests;

// Frozen pre-index factory lowering; do not call the production namespace plan.
internal static class NamespaceFactoryOracle
{
    private const string MapType = "global::System.Collections.Generic.IReadOnlyDictionary<global::System.Type, object>";
    public static void EmitFactory(EmissionContext _context, CSharpWriter writer, NamespaceScope scope, string name, string accessibility)
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
