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
    public IReadOnlyDictionary<LiteralAssignmentEmitter.Key, (string Alias, string Method, SharedGeneratedSource Source)> ScalarAssignments { get; init; } =
        new Dictionary<LiteralAssignmentEmitter.Key, (string Alias, string Method, SharedGeneratedSource Source)>(LiteralAssignmentEmitter.Keys);
    public IReadOnlyDictionary<string, (string Alias, string Method, SharedGeneratedSource Source)> Assignments { get; init; } =
        new Dictionary<string, (string Alias, string Method, SharedGeneratedSource Source)>(StringComparer.Ordinal);

    public static SharedPropertyTables?[] Create(BoundDocument[] documents, RoslynTypeSystem types,
        string generatedNamespace, CancellationToken cancellation)
    {
        if (documents.Length < 2) return new SharedPropertyTables?[documents.Length];
        // Collect repeated assignments with expected O(1) lookup. Only unique
        // shared groups/accessors are sorted at publication; output remains ordinal
        // and independent of dictionary enumeration or document completion order.
        var groups = new Dictionary<string, Dictionary<string, PropertyAccessor>>(StringComparer.Ordinal);
        var sharedGroups = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>?[documents.Length];
        var scalarKeys = new HashSet<LiteralAssignmentEmitter.Key>?[documents.Length];
        var scalarGroups = new Dictionary<string, Dictionary<LiteralAssignmentEmitter.Key, (BoundMember Member, int Count)>>(StringComparer.Ordinal);
        var assignmentGroups = new Dictionary<string, Dictionary<string, (BoundMember Member, int Count)>>(StringComparer.Ordinal);
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
                    var ownerName = PropertyAccessor.ReceiverType(owner.Type, set.Member).CSharpName();
                    if (!groups.TryGetValue(ownerName, out var group))
                        groups.Add(ownerName, group = new(StringComparer.Ordinal));
                    var key = accessor.Key;
                    if ((keys[i] ??= new(StringComparer.Ordinal)).Add(key) && group.ContainsKey(key)) sharedGroups.Add(ownerName);
                    group[key] = accessor;
                    var isScalar = LiteralAssignmentEmitter.TryKey(set, owner, out var scalar);
                    if (!isScalar && PropertyAssignmentEmitter.TryKey(set, owner, out var assignmentKey))
                    {
                        if (!assignmentGroups.TryGetValue(ownerName, out var assignments))
                            assignmentGroups.Add(ownerName, assignments = new(StringComparer.Ordinal));
                        assignments.TryGetValue(assignmentKey, out var current);
                        assignments[assignmentKey] = (set.Member, current.Count + 1);
                    }
                    if (isScalar &&
                        scalar.GeneratedDescriptor == null && (scalar.Descriptor == null ||
                            Accessible(scalar.Descriptor.ContainingType) && Accessible(scalar.Descriptor) &&
                            (scalar.Descriptor is not IPropertySymbol descriptorProperty ||
                                descriptorProperty.GetMethod is { } read && Accessible(read))))
                    {
                        if (!scalarGroups.TryGetValue(ownerName, out var scalars))
                            scalarGroups.Add(ownerName, scalars = new(LiteralAssignmentEmitter.Keys));
                        scalars.TryGetValue(scalar, out var current);
                        scalars[scalar] = (set.Member, current.Count + 1);
                        (scalarKeys[i] ??= new(LiteralAssignmentEmitter.Keys)).Add(scalar);
                    }
                }
            }
        }
        var registrations = new Dictionary<string, (string Alias, int Index, SharedGeneratedSource Source)>(StringComparer.Ordinal);
        var scalarRegistrations = new Dictionary<LiteralAssignmentEmitter.Key, (string Alias, string Method, SharedGeneratedSource Source)>(LiteralAssignmentEmitter.Keys);
        var assignmentRegistrations = new Dictionary<string, (string Alias, string Method, SharedGeneratedSource Source)>(StringComparer.Ordinal);
        using var hash = System.Security.Cryptography.SHA256.Create();
        var orderedGroups = sharedGroups.ToArray();
        Array.Sort(orderedGroups, StringComparer.Ordinal);
        foreach (var ownerName in orderedGroups)
        {
            cancellation.ThrowIfCancellationRequested();
            var group = groups[ownerName];
            var orderedKeys = group.Keys.ToArray();
            Array.Sort(orderedKeys, StringComparer.Ordinal);
            var accessors = new PropertyAccessor[orderedKeys.Length];
            var slots = new Dictionary<string, int>(orderedKeys.Length, StringComparer.Ordinal);
            for (var i = 0; i < orderedKeys.Length; i++)
            {
                accessors[i] = group[orderedKeys[i]];
                slots.Add(orderedKeys[i], i);
            }
            var body = new CSharpWriter { Indent = 2 };
            PropertyTableEmitter.Emit(body, accessors, "Table", "Get", "Set", "internal");
            var assignmentMethods = new Dictionary<string, string>(StringComparer.Ordinal);
            if (assignmentGroups.TryGetValue(ownerName, out var groupAssignments))
                foreach (var assignment in groupAssignments.Where(entry => entry.Value.Count >= 2).OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    var method = "Assign" + slots[assignment.Key];
                    assignmentMethods.Add(assignment.Key, method);
                    PropertyAssignmentEmitter.EmitHelper(body, assignment.Value.Member, method, slots[assignment.Key]);
                }
            var scalarMethods = new Dictionary<LiteralAssignmentEmitter.Key, string>(LiteralAssignmentEmitter.Keys);
            if (scalarGroups.TryGetValue(ownerName, out var groupScalars))
            {
                foreach (var scalar in groupScalars.Where(entry => entry.Value.Count >= 2)
                    .OrderBy(entry => LiteralAssignmentEmitter.StableKey(entry.Key, entry.Value.Member), StringComparer.Ordinal))
                {
                    var method = "SetScalar" + scalarMethods.Count;
                    scalarMethods.Add(scalar.Key, method);
                    var accessor = PropertyAccessor.Create(scalar.Key.Property.ContainingType, scalar.Value.Member);
                    LiteralAssignmentEmitter.EmitHelper(body, scalar.Key, scalar.Value.Member, method, "internal",
                        "Table.Register(__frame, " + slots[accessor.Key] + ");");
                }
            }
            // The complete body is emitted once. Put its identity in the namespace
            // so hashing never requires a second emission or rewriting user text.
            var bodyText = body.ToString();
            var id = CSharpNames.StableId(hash, bodyText);
            const string name = "Properties";
            var ns = generatedNamespace + ".Properties.P_" + id;
            var writer = new CSharpWriter();
            writer.Line("#nullable enable annotations"); writer.Line("#nullable disable warnings");
            writer.Open("namespace " + ns); writer.Open("internal static class " + name);
            writer.Append(bodyText);
            writer.Close(); writer.Close();
            var source = new SharedGeneratedSource("global::" + ns + "." + name, writer.ToString());
            for (var i = 0; i < orderedKeys.Length; i++) registrations.Add(orderedKeys[i], ("__p_" + id, i, source));
            foreach (var scalar in scalarMethods) scalarRegistrations.Add(scalar.Key, ("__p_" + id, scalar.Value, source));
            foreach (var assignment in assignmentMethods) assignmentRegistrations.Add(assignment.Key, ("__p_" + id, assignment.Value, source));
        }
        var result = new SharedPropertyTables?[documents.Length];
        for (var i = 0; i < result.Length; i++)
        {
            if (keys[i] is not { } documentKeys) continue;
            var entries = new Dictionary<string, (string Alias, int Index, SharedGeneratedSource Source)>(StringComparer.Ordinal);
            var sources = new Dictionary<string, SharedGeneratedSource>(StringComparer.Ordinal);
            foreach (var key in documentKeys)
            {
                if (!registrations.TryGetValue(key, out var entry)) continue;
                entries.Add(key, entry);
                sources[entry.Alias] = entry.Source;
            }
            if (entries.Count != 0)
            {
                var scalars = new Dictionary<LiteralAssignmentEmitter.Key, (string Alias, string Method, SharedGeneratedSource Source)>(LiteralAssignmentEmitter.Keys);
                if (scalarKeys[i] is { } documentScalars)
                    foreach (var key in documentScalars)
                        if (scalarRegistrations.TryGetValue(key, out var entry)) scalars.Add(key, entry);
                var assignments = new Dictionary<string, (string Alias, string Method, SharedGeneratedSource Source)>(StringComparer.Ordinal);
                foreach (var key in documentKeys)
                    if (assignmentRegistrations.TryGetValue(key, out var entry)) assignments.Add(key, entry);
                result[i] = new(entries, sources.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)).ToImmutableArray())
                    { ScalarAssignments = scalars, Assignments = assignments };
            }
        }
        return result;
    }
}
