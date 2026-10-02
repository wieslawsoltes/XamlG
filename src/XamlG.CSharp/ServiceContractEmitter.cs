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
        var contracts = _context.Document.Runtime.Services; if (contracts.Length == 0) return; var writer = _context.Writer;
        writer.Open("private sealed class " + _context.ServicesType + " : " + string.Join(", ", contracts.Select(s => s.InterfaceType.CSharpName()).Distinct()));
        writer.Line("private readonly " + CSharpNames.Context + " _context;");
        writer.Line("public " + _context.ServicesType + "(" + CSharpNames.Context + " context) => _context = context;");
        var emitted = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var contract in contracts)
            foreach (var member in contract.InterfaceType.Members())
            {
                if (member.IsStatic || !emitted.Add(member) || member is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet }) continue;
                if (member is not IPropertySymbol property || property.IsIndexer)
                { _context.Error($"Runtime contract member '{member}' is not a supported service property.", new(0, 0)); continue; }
                var expression = contract.Mapping.Kind switch
                {
                    XamlServiceKind.RootObject when property.Name == "RootObject" => "_context.RootObject",
                    XamlServiceKind.RootObject when property.Name == "IntermediateRootObject" => "_context.IntermediateRootObject",
                    XamlServiceKind.ProvideValueTarget when property.Name == "TargetObject" => "_context.TargetObject",
                    XamlServiceKind.ProvideValueTarget when property.Name == "TargetProperty" => "_context.TargetProperty",
                    XamlServiceKind.ParentStack when property.Name == "Parents" => "_context.Parents",
                    XamlServiceKind.UriContext when property.Name == "BaseUri" => "_context.BaseUri",
                    _ => null
                };
                if (expression == null) { _context.Error($"Service property '{property}' has no configured value mapping.", new(0, 0)); expression = "default"; }
                var getter = property.GetMethod == null ? string.Empty : "get => (" + property.Type.CSharpName() + ")" + expression + "!;";
                var setter = property.SetMethod == null ? string.Empty : contract.Mapping.Kind == XamlServiceKind.UriContext ? "set => _context.BaseUri = value;" : "set => throw new global::System.NotSupportedException();";
                writer.Line(property.Type.CSharpName() + " " + property.ContainingType.CSharpName() + "." + CSharpNames.Identifier(property.Name) + " { " + getter + " " + setter + " }");
            }
        writer.Close();
    }
}
