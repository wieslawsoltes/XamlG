using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;

namespace XamlG.CSharp;

/// <summary>Parameters of a shared typed construction body, never runtime instructions.</summary>
internal sealed class ConstructionParameters(BoundObject[] nodes, (BoundConstantExpression Value, ITypeSymbol Type)[] values)
{
    private const int LinearLimit = 8;
    private Dictionary<BoundObject, int>? _nodes;
    private Dictionary<BoundConstantExpression, string>? _constants;
    public BoundObject[] Nodes { get; } = nodes;
    public (BoundConstantExpression Value, ITypeSymbol Type)[] Values { get; } = values;

    public string? Key(BoundObject value) => NodeIndex(value) is var index && index >= 0 ? "__key" + index : null;
    public string? SourceIndex(BoundObject value) => NodeIndex(value) is var index && index >= 0 ? "__sourceIndex" + index : null;
    private int NodeIndex(BoundObject value)
    {
        if (Nodes.Length <= LinearLimit)
        {
            for (var index = 0; index < Nodes.Length; index++) if (ReferenceEquals(Nodes[index], value)) return index;
            return -1;
        }
        if (_nodes == null)
        {
            var indexes = new Dictionary<BoundObject, int>(Nodes.Length, Identity<BoundObject>.Instance);
            for (var index = 0; index < Nodes.Length; index++)
                if (!indexes.ContainsKey(Nodes[index])) indexes.Add(Nodes[index], index);
            _nodes = indexes;
        }
        return _nodes.TryGetValue(value, out var result) ? result : -1;
    }

    public string? Constant(BoundConstantExpression value)
    {
        if (Values.Length <= LinearLimit)
        {
            for (var index = 0; index < Values.Length; index++) if (ReferenceEquals(Values[index].Value, value)) return "__literal" + index;
            return null;
        }
        if (_constants == null)
        {
            // Build only when a helper body is emitted, not for every occurrence
            // discovered by the shape planner. First occurrence wins, exactly as
            // in the bounded linear path. Never hash bound records structurally.
            var names = new Dictionary<BoundConstantExpression, string>(Values.Length, Identity<BoundConstantExpression>.Instance);
            for (var index = 0; index < Values.Length; index++)
                if (!names.ContainsKey(Values[index].Value)) names.Add(Values[index].Value, "__literal" + index);
            _constants = names;
        }
        return _constants.TryGetValue(value, out var name) ? name : null;
    }

    private sealed class Identity<T> : IEqualityComparer<T> where T : class
    {
        public static readonly Identity<T> Instance = new();
        public bool Equals(T? left, T? right) => ReferenceEquals(left, right);
        public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
    }
}
