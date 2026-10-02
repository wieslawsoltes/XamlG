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
        var contracts = _context.Document.Runtime.Services;
        if (contracts.Length == 0) return;
        var writer = _context.Writer;
        writer.Open("private sealed class " + _context.ServicesType + " : " + string.Join(", ", contracts.Select(s => s.InterfaceType.CSharpName()).Distinct()));
        writer.Line("private readonly " + CSharpNames.Context + " _context;");
        writer.Line("public " + _context.ServicesType + "(" + CSharpNames.Context + " context) => _context = context;");
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
        _ => throw new ArgumentOutOfRangeException(nameof(property))
    };
}
