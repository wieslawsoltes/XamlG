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
    private readonly SourceInfoEmitter _source;
    public ObjectEmitter(EmissionContext context)
    {
        _context = context; _namespaces = new(context); _runtime = new(context, _namespaces);
        _values = new(context, this); _assignments = new(context, this, _values); _source = new(context);
    }
    public void EmitContext(string variable, string outer, string root) => _runtime.Create(variable, outer, root);
    public void RegisterName(string frame, string nameExpression, string value) => _runtime.RegisterName(frame, nameExpression, value);
    public void Complete(string frame, string root) => _runtime.Complete(frame, root);
    public void EmitNamespaceMaps() => _namespaces.Emit();
    public void EmitContextHelpers() => _runtime.EmitHelpers();
    public string ConstructRoot(BoundObject value, string parentContext) =>
        Construct(value, _runtime.Scope(parentContext, value.Scope), _context.RootVariable);

    private string Construct(BoundObject value, string parentContext, string variable)
    {
        var arguments = value.Arguments.IsDefaultOrEmpty ? Array.Empty<string>() :
            _values.EmitArguments(value.FactoryMethod ?? value.Constructor!, value.Arguments, parentContext);
        var creation = value.FactoryMethod != null
            ? value.FactoryMethod.ContainingType.CSharpName() + "." + CSharpNames.Method(value.FactoryMethod) + "(" + string.Join(", ", arguments) + ")"
            : "new " + value.Type.CSharpName() + "(" + string.Join(", ", arguments) + ")";
        _context.Writer.Line("var " + variable + " = " + creation + ";");
        _context.Writer.Line(parentContext + ".Session.TrackConstruction(" + variable + ");");
        _source.EmitConstructed(value, variable);
        return variable;
    }

    public void EmitDeferredContext(string variable, string parent, string incoming, bool functionPointer)
    {
        if (functionPointer) _runtime.Create(variable, incoming, "null", deferred: true);
        else
        {
            _context.Writer.Line("var " + variable + " = " + parent + ".CreateDeferredScope(" + incoming + ");");
            _context.InheritFrameNamespaces(variable, parent);
            _runtime.InitializeNameScope(variable, incoming);
        }
    }
    public string Emit(BoundObject value, string parentContext, string? existing, Action<string>? consume, Action<string>? initialize = null)
    {
        _context.Cancellation.ThrowIfCancellationRequested(); var writer = _context.Writer;
        parentContext = _runtime.Scope(parentContext, value.Scope);
        var variable = existing ?? (value.IsRoot ? _context.RootVariable : _context.Temporary("object"));
        if (existing == null)
            Construct(value, parentContext, variable);
        var frame = _context.Temporary("context");
        writer.Line("var " + frame + " = " + parentContext + (value.IsRoot ? ".PushRoot(" : ".Push(") + variable + ", " + CSharpNames.Literal(value.Key) + ");");
        _context.InheritFrameNamespaces(frame, parentContext);
        _source.Emit(value, frame);
        if (value.Name != null)
        {
            if (!value.Assignments.OfType<BoundSetAssignment>().Any(assignment => assignment.RegisterName))
                _runtime.RegisterName(frame, CSharpNames.Literal(value.Name), variable);
        }
        if (_context.Document.ClassSymbol != null && _context.Document.CanAugmentClass && _context.Document.Options.GenerateNamedFields)
            foreach (var field in _context.NamedFields.Where(field => ReferenceEquals(field.Object, value)))
                writer.Line(_context.RootVariable + "." + CSharpNames.Identifier(field.Name) + " = " + variable + ";");
        if (value.SupportsInitialize) writer.Line("((global::System.ComponentModel.ISupportInitialize)" + variable + ").BeginInit();");
        initialize?.Invoke(variable);
        if (value.UsableDuringInitialization) consume?.Invoke(variable);
        foreach (var assignment in value.Assignments)
            _context.Map(assignment.Span, () => _assignments.Emit(assignment, value, variable, frame));
        if (value.SupportsInitialize) writer.Line("((global::System.ComponentModel.ISupportInitialize)" + variable + ").EndInit();");
        if (!value.UsableDuringInitialization) consume?.Invoke(variable);
        return variable;
    }
}
