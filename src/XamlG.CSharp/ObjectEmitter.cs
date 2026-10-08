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
    private readonly LeafConstructionEmitter _leaves;
    public ObjectEmitter(EmissionContext context)
    {
        _context = context; _namespaces = new(context); _runtime = new(context, _namespaces);
        _values = new(context, this); _assignments = new(context, this, _values); _source = new(context);
        _leaves = new(context, _values, _source);
    }
    public void EmitContext(string variable, string outer, string root) => _runtime.Create(variable, outer, root);
    public void RegisterName(string frame, string nameExpression, string value) => _runtime.RegisterName(frame, nameExpression, value);
    public void Complete(string frame, string root) => _runtime.Complete(frame, root);
    public void EmitNamespaceMaps() => _namespaces.Emit();
    public void EmitContextHelpers() { _leaves.EmitHelpers(); _runtime.EmitHelpers(); }
    public string ConstructRoot(BoundObject value, string parentContext) =>
        Construct(value, _runtime.Scope(parentContext, value.Scope), _context.RootVariable);

    private string Construct(BoundObject value, string parentContext, string? variable, bool track = true)
    {
        var arguments = value.Arguments.IsDefaultOrEmpty ? Array.Empty<string>() :
            _values.EmitArguments(value.FactoryMethod ?? value.Constructor!, value.Arguments, parentContext);
        var creation = value.FactoryMethod != null
            ? value.FactoryMethod.ContainingType.CSharpName() + "." + CSharpNames.Method(value.FactoryMethod) + "(" + string.Join(", ", arguments) + ")"
            : "new " + value.Type.CSharpName() + "(" + string.Join(", ", arguments) + ")";
        if (variable == null) variable = _context.Locals.Declare((value.FactoryMethod?.ReturnType ?? value.Type).CSharpName(), creation, "object", inferred: true);
        else _context.Writer.Line("var " + variable + " = " + creation + ";");
        if (track) _context.Writer.Line(parentContext + ".Session.TrackConstruction(" + variable + ");");
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
        if (existing == null && consume == null && initialize == null && _leaves.CanShare(value))
            return _leaves.Emit(value, parentContext);
        // Framework source-info setters can execute user code and must remain after
        // tracking but before node registration. Preserve that sequence when enabled.
        var trackWithFrame = existing == null && !value.IsRoot && _context.Document.Runtime.SourceInfo == null;
        var variable = existing ?? Construct(value, parentContext, value.IsRoot ? _context.RootVariable : null, track: !trackWithFrame);
        var frame = _context.Locals.Declare(CSharpNames.Context, parentContext + (value.IsRoot ? ".PushRoot(" : trackWithFrame ? ".PushConstructed(" : ".Push(") + variable + ", " + CSharpNames.Literal(value.Key) + ", " + _source.Get(value) + ")", "context", inferred: true);
        _context.InheritFrameNamespaces(frame, parentContext);
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
