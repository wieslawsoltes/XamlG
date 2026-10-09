using Microsoft.CodeAnalysis;
using XamlG.Compiler;

namespace XamlG.CSharp;

/// <summary>Parameters of a shared typed construction body, never runtime instructions.</summary>
internal sealed record ConstructionParameters(BoundObject[] Nodes, (BoundConstantExpression Value, ITypeSymbol Type)[] Values)
{
    public string? Key(BoundObject value) => NodeIndex(value) is var index && index >= 0 ? "__key" + index : null;
    public string? SourceIndex(BoundObject value) => NodeIndex(value) is var index && index >= 0 ? "__sourceIndex" + index : null;
    private int NodeIndex(BoundObject value)
    {
        for (var index = 0; index < Nodes.Length; index++) if (ReferenceEquals(Nodes[index], value)) return index;
        return -1;
    }
    public string? Constant(BoundConstantExpression value)
    {
        for (var index = 0; index < Values.Length; index++) if (ReferenceEquals(Values[index].Value, value)) return "__literal" + index;
        return null;
    }
}
