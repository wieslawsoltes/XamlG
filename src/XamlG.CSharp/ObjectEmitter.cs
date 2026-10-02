using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

internal sealed class ObjectEmitter
{
    private readonly EmissionContext _context;
    private readonly ValueEmitter _values;
    private readonly AssignmentEmitter _assignments;
    private readonly NamespaceMapEmitter _namespaces;
    private readonly RuntimeContextEmitter _runtime;

    public ObjectEmitter(EmissionContext context)
    {
        _context = context;
        _namespaces = new(context);
        _runtime = new(context, _namespaces);
        _values = new(context, this);
        _assignments = new(context, this, _values);
    }

    public void EmitContext(string variable, string outer, string root) => _runtime.Create(variable, outer, root);
    public void Complete(string frame, string root) => _runtime.Complete(frame, root);
    public void EmitNamespaceMaps() => _namespaces.Emit();

    public void EmitDeferredContext(string variable, string parent, string incoming, bool functionPointer)
    {
        if (functionPointer)
            _runtime.Create(variable, incoming, "null", deferred: true);
        else
        {
            _context.Writer.Line("var " + variable + " = " + parent + ".CreateDeferredScope(" + incoming + ");");
            _runtime.InitializeNameScope(variable, incoming);
        }
    }

    public string Emit(BoundObject value, string parentContext, string? existing, Action<string>? consume)
    {
        _context.Cancellation.ThrowIfCancellationRequested();
        var writer = _context.Writer;
        parentContext = _runtime.Scope(parentContext, value.Scope);
        var variable = existing ?? (value.IsRoot ? _context.RootVariable : _context.Temporary("object"));
        if (existing == null)
        {
            var arguments = value.Arguments.Select(a => _values.Emit(a, parentContext)).ToArray();
            var creation = value.FactoryMethod != null
                ? value.FactoryMethod.ContainingType.CSharpName() + "." + CSharpNames.Method(value.FactoryMethod) + "(" + string.Join(", ", arguments) + ")"
                : "new " + value.Type.CSharpName() + "(" + string.Join(", ", arguments) + ")";
            writer.Line("var " + variable + " = " + creation + ";");
        }

        var frame = _context.Temporary("context");
        writer.Line("var " + frame + " = " + parentContext + ".Push(" + variable + ", " + CSharpNames.Literal(value.Key) + ");");
        if (value.Name != null)
        {
            _runtime.RegisterName(frame, value.Name, variable);
            if (_context.Document.ClassSymbol != null && _context.Document.Options.GenerateNamedFields &&
                value.NameScopeId == _context.Document.Root!.NameScopeId)
                writer.Line(_context.RootVariable + "." + CSharpNames.Identifier(value.Name) + " = " + variable + ";");
        }

        if (value.SupportsInitialize)
            writer.Line("((global::System.ComponentModel.ISupportInitialize)" + variable + ").BeginInit();");
        if (value.UsableDuringInitialization) consume?.Invoke(variable);

        var collections = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        foreach (var assignment in value.Assignments)
            _context.Map(assignment.Span, () => _assignments.Emit(assignment, value, variable, frame, collections));

        if (value.SupportsInitialize)
            writer.Line("((global::System.ComponentModel.ISupportInitialize)" + variable + ").EndInit();");
        if (!value.UsableDuringInitialization) consume?.Invoke(variable);
        return variable;
    }
}
