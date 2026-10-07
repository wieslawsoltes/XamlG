namespace XamlG.Compiler;

/// <summary>The property whose value is currently being bound, including nested object values.</summary>
public sealed class XamlPropertyBindingScope : IDisposable
{
    private readonly BindingContext _context;
    internal XamlPropertyBindingScope(BindingContext context, ObjectBindingBuilder target, BoundMember member)
    {
        _context = context; Target = target; Member = member;
        Parent = context.PropertyScope;
        context.PropertyScope = this;
    }
    public ObjectBindingBuilder Target { get; }
    public BoundMember Member { get; }
    public XamlPropertyBindingScope? Parent { get; }
    public void Dispose() => _context.PropertyScope = Parent;

    internal static IDisposable Suspend(BindingContext context) => new Suspension(context);

    private sealed class Suspension : IDisposable
    {
        private readonly BindingContext _context;
        private readonly XamlPropertyBindingScope? _previous;
        public Suspension(BindingContext context)
        {
            _context = context; _previous = context.PropertyScope;
            context.PropertyScope = null;
        }
        public void Dispose() => _context.PropertyScope = _previous;
    }
}
