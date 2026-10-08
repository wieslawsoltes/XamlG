using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

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
        // The class contains only service-contract operations and a supplied frame;
        // document namespaces, roots and target objects remain in that frame.
        var identity = new CSharpWriter();
        Write(identity, "Services", "internal");
        var name = "Services_" + _context.StableId(identity.ToString());
        var ns = _context.Document.Options.GeneratedNamespace + ".Services";
        var writer = new CSharpWriter();
        writer.Line("#nullable enable annotations");
        writer.Line("#nullable disable warnings");
        writer.Open("namespace " + ns);
        Write(writer, name, "internal");
        writer.Close();
        return new("global::" + ns + "." + name, writer.ToString());
    }

    private void Write(CSharpWriter writer, string name, string accessibility)
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
