using System.Collections;
using System.Collections.Immutable;
using XamlG.Compiler;

namespace XamlG.CSharp;

/// <summary>A small ordered view over existing edges. Common scalar/call/array
/// shapes enumerate without arrays, immutable-array insertion, boxing or iterators.
/// Compound initializer/choice/builder shapes use one lazily enumerated remainder.</summary>
internal readonly struct BoundExpressionSequence : IEnumerable<BoundExpression>
{
    private readonly bool _hasParts;
    private readonly BoundExpression? _first;
    private readonly ImmutableArray<BoundExpression> _values;
    private readonly ImmutableArray<BoundExpression> _after;
    private readonly BoundExpression? _last;
    private readonly IEnumerable<BoundExpression>? _remainder;

    public BoundExpressionSequence(BoundExpression? first, ImmutableArray<BoundExpression> values,
        ImmutableArray<BoundExpression> after, BoundExpression? last, IEnumerable<BoundExpression>? remainder)
    { _hasParts = true; _first = first; _values = values; _after = after; _last = last; _remainder = remainder; }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<BoundExpression> IEnumerable<BoundExpression>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal struct Enumerator : IEnumerator<BoundExpression>
    {
        private readonly BoundExpressionSequence _sequence;
        private IEnumerator<BoundExpression>? _remainder;
        private BoundExpression? _current;
        private int _phase;
        private int _index;
        public Enumerator(BoundExpressionSequence sequence)
        { _sequence = sequence; _remainder = null; _current = null; _phase = sequence._hasParts ? 0 : 5; _index = 0; }
        public BoundExpression Current => _current!;
        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            while (true)
            {
                switch (_phase)
                {
                    case 0:
                        _phase = 1;
                        if (_sequence._first is { } first) { _current = first; return true; }
                        break;
                    case 1:
                        if (_index < _sequence._values.Length) { _current = _sequence._values[_index++]; return true; }
                        _index = 0; _phase = 2;
                        break;
                    case 2:
                        if (_index < _sequence._after.Length) { _current = _sequence._after[_index++]; return true; }
                        _phase = 3;
                        break;
                    case 3:
                        _phase = 4;
                        if (_sequence._last is { } last) { _current = last; return true; }
                        break;
                    case 4:
                        if (_sequence._remainder != null)
                        {
                            _remainder ??= _sequence._remainder.GetEnumerator();
                            if (_remainder.MoveNext()) { _current = _remainder.Current; return true; }
                        }
                        Dispose();
                        return false;
                    default: return false;
                }
            }
        }
        public void Dispose()
        {
            _phase = 5; _current = null;
            var remainder = _remainder; _remainder = null;
            remainder?.Dispose();
        }
        void IEnumerator.Reset() => throw new NotSupportedException();
    }
}
