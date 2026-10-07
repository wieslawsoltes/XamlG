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
}
