using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

/// <summary>Immutable, content-addressed typed dispatch. Its identity includes the complete
/// accessor layout so cached registrations cannot be reused with different slot indexes.</summary>
internal sealed record SharedPropertyTables(
    IReadOnlyDictionary<string, (string Alias, int Index, SharedGeneratedSource Source)> Registrations,
    ImmutableArray<(string Alias, SharedGeneratedSource Source)> Sources)
{
    public string Identity { get; } = string.Join("\n", Sources.Select(entry => entry.Source.TypeName));

    public static SharedPropertyTables?[] Create(BoundDocument[] documents, RoslynTypeSystem types,
        string generatedNamespace, CancellationToken cancellation)
    {
        if (documents.Length < 2) return new SharedPropertyTables?[documents.Length];
        var groups = new SortedDictionary<string, SortedDictionary<string, PropertyAccessor>>(StringComparer.Ordinal);
        var sharedGroups = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>?[documents.Length];
        var accessible = new Dictionary<ISymbol, bool>(SymbolEqualityComparer.Default);
        bool Accessible(ISymbol symbol)
        {
            if (!accessible.TryGetValue(symbol, out var result)) accessible.Add(symbol, result = types.IsAccessible(symbol));
            return result;
        }
        for (var i = 0; i < documents.Length; i++)
        {
            var document = documents[i];
            if (!document.Success || document.Root == null || document.ClassSymbol?.IsGenericType == true) continue;
            foreach (var owner in BoundTraversal.Objects(document.Root, includeDeferred: true))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!owner.Type.IsReferenceType || !Accessible(owner.Type)) continue;
                foreach (var assignment in owner.Assignments)
                {
                    if (assignment is not BoundSetAssignment { Member.Getter: { } getter, Member.Setter: { IsInitOnly: false } setter } set ||
                        !Accessible(getter) || !Accessible(setter) || !Accessible(set.Member.ValueType)) continue;
                    // Document-generated descriptors and private code-behind members must
                    // remain in the document that declares them.
                    if (set.Member.StaticSetter is { } custom && (!Accessible(custom.Method) ||
                        custom.Descriptors.Any(descriptor => descriptor.GeneratedMemberName != null || !Accessible(descriptor.Member)))) continue;
                    var accessor = PropertyAccessor.Create(owner.Type, set.Member);
                    var ownerName = owner.Type.CSharpName();
                    if (!groups.TryGetValue(ownerName, out var group))
                        groups.Add(ownerName, group = new(StringComparer.Ordinal));
                    var key = accessor.Key;
                    if ((keys[i] ??= new(StringComparer.Ordinal)).Add(key) && group.ContainsKey(key)) sharedGroups.Add(ownerName);
                    group[key] = accessor;
                }
            }
        }
        var registrations = new Dictionary<string, (string Alias, int Index, SharedGeneratedSource Source)>(StringComparer.Ordinal);
        using var hash = System.Security.Cryptography.SHA256.Create();
        foreach (var pair in groups)
        {
            if (!sharedGroups.Contains(pair.Key)) continue;
            cancellation.ThrowIfCancellationRequested();
            var group = pair.Value;
            var accessors = group.Values.ToArray();
            var body = new CSharpWriter();
            PropertyTableEmitter.Emit(body, accessors, "Table", "Get", "Set", "internal");
            var id = CSharpNames.StableId(hash, body.ToString());
            var name = "Properties_" + id;
            var ns = generatedNamespace + ".Properties";
            var writer = new CSharpWriter();
            writer.Line("#nullable enable annotations"); writer.Line("#nullable disable warnings");
            writer.Open("namespace " + ns); writer.Open("internal static class " + name);
            PropertyTableEmitter.Emit(writer, accessors, "Table", "Get", "Set", "internal");
            writer.Close(); writer.Close();
            var source = new SharedGeneratedSource("global::" + ns + "." + name, writer.ToString());
            var index = 0;
            foreach (var key in group.Keys) registrations.Add(key, ("__p_" + id, index++, source));
        }
        var result = new SharedPropertyTables?[documents.Length];
        for (var i = 0; i < result.Length; i++)
        {
            if (keys[i] is not { } documentKeys) continue;
            var entries = new Dictionary<string, (string Alias, int Index, SharedGeneratedSource Source)>(StringComparer.Ordinal);
            var sources = new SortedDictionary<string, SharedGeneratedSource>(StringComparer.Ordinal);
            foreach (var key in documentKeys)
            {
                if (!registrations.TryGetValue(key, out var entry)) continue;
                entries.Add(key, entry);
                sources[entry.Alias] = entry.Source;
            }
            if (entries.Count != 0) result[i] = new(entries, sources.Select(pair => (pair.Key, pair.Value)).ToImmutableArray());
        }
        return result;
    }
}
