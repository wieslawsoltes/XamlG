using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.CSharp;

internal sealed class ServiceContractEmitter
{
    private readonly EmissionContext _context;
    public ServiceContractEmitter(EmissionContext context) => _context = context;

    public void Emit()
    {
        if (_context.SharedServices != null) return;
        Write(_context.Writer, _context.ServicesType, "private");
    }

    public SharedServiceSource CreateShared()
    {
        // Share the code for service contracts and namespace maps. Each document
        // still owns its map instances, roots and target objects.
        var scopes = new SortedDictionary<string, NamespaceScope>(StringComparer.Ordinal);
        if (_context.Document.Runtime.Services.Any(service => service.Mapping.Kind == XamlServiceKind.XmlNamespaces))
        {
            var visited = new HashSet<NamespaceScope>();
            foreach (var value in BoundTraversal.Objects(_context.Document.Root!, includeDeferred: true))
            {
                _context.Cancellation.ThrowIfCancellationRequested();
                if (visited.Add(value.Scope)) scopes[NamespaceMapEmitter.ScopeKey(value.Scope)] = value.Scope;
            }
        }
        var factories = scopes.Select((entry, index) => (entry.Key, Name: "CreateNamespaces" + index, Scope: entry.Value)).ToArray();
        // Hash the body we will actually emit. Keep its type name fixed and put
        // the identity in the namespace, avoiding a second namespace-map/context
        // emission and any replacement inside user-provided namespace literals.
        var identity = new CSharpWriter { Indent = 1 };
        Write(identity, "Services", "internal", factories);
        var body = identity.ToString();
        var ns = _context.Document.Options.GeneratedNamespace + ".Services_" + _context.StableId(body);
        var source = "#nullable enable annotations\n#nullable disable warnings\nnamespace " + ns + "\n{\n" + body + "}\n";
        return new("global::" + ns + ".Services", source,
            factories.ToImmutableDictionary(factory => factory.Key, factory => factory.Name, StringComparer.Ordinal));
    }

    private void Write(CSharpWriter writer, string name, string accessibility,
        (string Key, string Name, NamespaceScope Scope)[]? factories = null)
    {
        var contracts = _context.Document.Runtime.Services;
        if (contracts.Length == 0) return;
        writer.Open(accessibility + " sealed class " + name + " : " + string.Join(", ", contracts.Select(s => s.ImplementationType.CSharpName()).Distinct()));
        writer.Line("private readonly " + CSharpNames.Context + " _context;");
        writer.Line("public " + name + "(" + CSharpNames.Context + " context) => _context = context;");
        var emitted = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var contract in contracts)
        {
            foreach (var bound in contract.Properties)
            {
                var property = bound.Property;
                if (!emitted.Add(property)) continue;
                var expression = Value(contract, bound);
                var getter = "get => (" + property.Type.CSharpName() + ")" + expression + "!;";
                var setter = property.SetMethod == null ? string.Empty : "set => _context.BaseUri = value;";
                writer.Line(property.Type.CSharpName() + " " + property.ContainingType.CSharpName() + "." + CSharpNames.Identifier(property.Name) + " { " + getter + " " + setter + " }");
            }
        }
        if (factories != null)
        {
            var namespaces = new NamespaceMapEmitter(_context);
            foreach (var factory in factories) namespaces.EmitFactory(writer, factory.Scope, factory.Name, "public");
            new SharedRuntimeContextEmitter(_context.Document).Emit(writer, name);
        }
        writer.Close();
    }

    private static string Value(BoundServiceContract contract, BoundServiceProperty property) => property.Value switch
    {
        XamlServiceValue.RootObject => "_context.RootObject",
        XamlServiceValue.IntermediateRootObject => "_context.IntermediateRootObject",
        XamlServiceValue.TargetObject => "_context.TargetObject",
        XamlServiceValue.TargetProperty => "_context.TargetProperty",
        XamlServiceValue.BaseUri => "_context.BaseUri",
        XamlServiceValue.XmlNamespaces => "_context.GetNamespaceValue(typeof(" + contract.InterfaceType.CSharpName() + "))",
        XamlServiceValue.Parents => "_context.EnumerateParents(typeof(" + contract.InterfaceType.CSharpName() + "), static __provider => ((" + contract.InterfaceType.CSharpName() + ")__provider)." + CSharpNames.Identifier(property.Property.Name) + ")",
        XamlServiceValue.DirectParents => "_context.DirectParentsStack",
        XamlServiceValue.ParentProvider => "(_context.GetExternalService(typeof(" + contract.InterfaceType.CSharpName() + ")) is " + contract.InterfaceType.CSharpName() +
            " __parent ? " + contract.ParentProviderAdapter!.ContainingType.CSharpName() + "." + CSharpNames.Method(contract.ParentProviderAdapter) + "(__parent) : null)",
        _ => throw new ArgumentOutOfRangeException(nameof(property))
    };
}
