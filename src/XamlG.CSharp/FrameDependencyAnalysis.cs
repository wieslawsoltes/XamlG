using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using XamlG.Compiler;

namespace XamlG.CSharp;

/// <summary>Pure, occurrence-independent frame-use analysis. Never memoize emitted
/// execution. All discovery state dies with the query, not with the compilation.</summary>
internal static class FrameDependencyAnalysis
{
    public static bool UsesFrame(BoundExpression value)
    {
        // Bound recursive branching, not unary depth. Ordinary literals and small
        // expressions need no iterator, visited set, stack array or retained cache.
        var budget = 32;
        return TrySmall(value, ref budget, out var result) ? result : Scan(value);
    }

    private static bool TrySmall(BoundExpression value, ref int budget, out bool result)
    {
        if (budget-- == 0) { result = false; return false; }
        if (value is BoundConstantExpression) { result = false; return true; }
        // Tail-eliminate unary chains. At most 32 invocations can each traverse
        // such a chain before the query switches to exact memoization. This is a
        // constant work multiplier, not an exponential depth allowance.
        while (SingleChild(value) is { } child) value = child;
        if (Known(value, out result)) return true;
        var children = new Children(value);
        while (children.MoveNext(out var next))
        {
            if (!TrySmall(next, ref budget, out result)) return false;
            if (result) return true;
        }
        result = false;
        return true;
    }

    private static bool Scan(BoundExpression value)
    {
        HashSet<BoundExpression>? independent = null;
        Stack<Children>? parents = null;
        var current = new Children(value);
        while (true)
        {
            if (!current.MoveNext(out var child))
            {
                if (parents == null || parents.Count == 0) return false;
                // Only completed false results enter the set. A positive leaf
                // ends this whole OR query; partial results never escape.
                (independent ??= new(Identity.Instance)).Add(current.Owner);
                current = parents.Pop();
                continue;
            }
            if (Known(child, out var required))
            {
                if (required) return true;
                continue;
            }
            if (independent?.Contains(child) == true) continue;
            (parents ??= new(16)).Push(current);
            current = new(child);
        }
    }

    // Retain the old conservative boundary. Converter, object, markup, deferred,
    // choice and unknown/custom IR require a frame even if their children do not.
    private static bool Known(BoundExpression value, out bool result)
    {
        switch (value)
        {
            case BoundConstantExpression or BoundEnumExpression or BoundTypeExpression or
                BoundMethodHandleExpression or BoundStaticExpression or BoundParseExpression or BoundParameterExpression:
                result = false; return true;
            case BoundCastExpression or BoundCachedExpression or BoundNewExpression or BoundBuilderExpression or
                BoundScopedInitializationExpression or BoundArrayExpression or BoundCollectionExpression or
                BoundCallExpression or BoundPropertyAccessExpression or BoundFieldAccessExpression or
                BoundAssignmentExpression or BoundMethodGroupExpression or BoundLambdaExpression:
                result = false; return false;
            default:
                result = true; return true;
        }
    }

    private static BoundExpression? SingleChild(BoundExpression value) => value switch
    {
        BoundCastExpression x => x.Value,
        BoundCachedExpression x => x.Value,
        BoundFieldAccessExpression x => x.Receiver,
        BoundLambdaExpression x => x.Body,
        BoundMethodGroupExpression x => x.Receiver,
        BoundArrayExpression { Values.Length: 1 } x => x.Values[0],
        BoundCollectionExpression { Values.Length: 1 } x => x.Values[0],
        BoundCallExpression { Arguments.IsEmpty: true } x => x.Receiver,
        BoundPropertyAccessExpression { IndexArguments.IsEmpty: true } x => x.Receiver,
        BoundBuilderExpression { Calls.IsEmpty: true } x => x.Creation,
        BoundScopedInitializationExpression { Calls.IsEmpty: true } x => x.Creation,
        BoundNewExpression { Arguments.Length: 1, Initializers.IsEmpty: true } x => x.Arguments[0],
        _ => null
    };

    // One small value cursor over the existing IR arrays, with no iterator objects
    // for constructor initializers or nested builder/scoped call arguments.
    private struct Children(BoundExpression owner)
    {
        public BoundExpression Owner { get; } = owner;
        private int _index;
        private int _call;
        private int _argument;

        public bool MoveNext(out BoundExpression child)
        {
            switch (Owner)
            {
                case BoundCastExpression x: return Single(x.Value, out child);
                case BoundCachedExpression x: return Single(x.Value, out child);
                case BoundFieldAccessExpression x: return Single(x.Receiver, out child);
                case BoundLambdaExpression x: return Single(x.Body, out child);
                case BoundMethodGroupExpression x: return FirstThen(x.Receiver, [], out child);
                case BoundArrayExpression x: return Array(x.Values, out child);
                case BoundCollectionExpression x: return Array(x.Values, out child);
                case BoundAssignmentExpression x:
                    if (_index == 0) { _index++; child = x.Target; return true; }
                    if (_index == 1) { _index++; child = x.Value; return true; }
                    break;
                case BoundCallExpression x: return FirstThen(x.Receiver, x.Arguments, out child);
                case BoundPropertyAccessExpression x: return FirstThen(x.Receiver, x.IndexArguments, out child);
                case BoundNewExpression x:
                    if (_index < x.Arguments.Length) { child = x.Arguments[_index++]; return true; }
                    var initializer = _index - x.Arguments.Length;
                    if (initializer < x.Initializers.Length) { _index++; child = x.Initializers[initializer].Value; return true; }
                    break;
                case BoundBuilderExpression x: return Calls(x.Creation, x.Calls, out child);
                case BoundScopedInitializationExpression x: return Calls(x.Creation, x.Calls, out child);
            }
            child = null!;
            return false;
        }

        private bool Single(BoundExpression value, out BoundExpression child)
        { child = value; return _index++ == 0; }

        private bool Array(ImmutableArray<BoundExpression> values, out BoundExpression child)
        {
            if (_index < values.Length) { child = values[_index++]; return true; }
            child = null!; return false;
        }

        private bool FirstThen(BoundExpression? first, ImmutableArray<BoundExpression> values, out BoundExpression child)
        {
            if (_index == 0)
            {
                _index = 1;
                if (first != null) { child = first; return true; }
            }
            if (_index - 1 < values.Length) { child = values[_index++ - 1]; return true; }
            child = null!; return false;
        }

        private bool Calls(BoundExpression creation, ImmutableArray<BoundBuilderCall> calls, out BoundExpression child)
        {
            if (_index == 0) { _index = 1; child = creation; return true; }
            while (_call < calls.Length)
            {
                var arguments = calls[_call].Arguments;
                if (_argument < arguments.Length) { child = arguments[_argument++]; return true; }
                _call++; _argument = 0;
            }
            child = null!; return false;
        }
    }

    private sealed class Identity : IEqualityComparer<BoundExpression>
    {
        public static readonly Identity Instance = new();
        public bool Equals(BoundExpression? x, BoundExpression? y) => ReferenceEquals(x, y);
        public int GetHashCode(BoundExpression value) => RuntimeHelpers.GetHashCode(value);
    }
}
