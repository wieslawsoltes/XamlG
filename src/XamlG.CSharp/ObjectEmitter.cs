using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
namespace XamlG.CSharp;
internal sealed class ObjectEmitter
{
    private readonly EmissionContext _context;
    private readonly ValueEmitter _values;
    private readonly AssignmentEmitter _assignments;
    public ObjectEmitter(EmissionContext context) { _context = context; _values = new(context, this); _assignments = new(context, this, _values); }
    public void EmitContext(string variable, string outer, string root)
    {
        var baseUri = _context.Document.Options.BaseUri is { } uri ? "new global::System.Uri(" + CSharpNames.Literal(uri) + ", global::System.UriKind.RelativeOrAbsolute)" : "null";
        var factory = _context.Document.Runtime.InnerServiceProviderFactory;
        if (factory != null) outer = factory.ContainingType.CSharpName() + "." + CSharpNames.Method(factory) + "(" + outer + ")";
        var serviceTypes = _context.Document.Runtime.Services.Select(s => "__type == typeof(" + s.InterfaceType.CSharpName() + ")").ToArray();
        var services = serviceTypes.Length == 0 ? "null" : "static (__frame, __type) => (" + string.Join(" || ", serviceTypes) + ") ? new " + _context.ServicesType + "(__frame) : null";
        _context.Writer.Line("var " + variable + " = new " + CSharpNames.Context + "(" + outer + ", " + root + ", " + baseUri + ", " + services + ");");
    }
    public string Emit(BoundObject value, string parentContext, string? existing, Action<string>? consume)
    {
        _context.Cancellation.ThrowIfCancellationRequested(); var writer = _context.Writer;
        var variable = existing ?? (value.IsRoot ? _context.RootVariable : _context.Temporary("object"));
        if (existing == null)
        {
            var arguments = value.Arguments.Select(a => _values.Emit(a, parentContext)).ToArray();
            string creation;
            if (value.FactoryMethod != null) creation = value.FactoryMethod.ContainingType.CSharpName() + "." + CSharpNames.Method(value.FactoryMethod) + "(" + string.Join(", ", arguments) + ")";
            else creation = "new " + value.Type.CSharpName() + "(" + string.Join(", ", arguments) + ")";
            writer.Line("var " + variable + " = " + creation + ";");
        }
        var frame = _context.Temporary("context");
        writer.Line("var " + frame + " = " + parentContext + ".Push(" + variable + ", " + CSharpNames.Literal(value.Key) + ");");
        if (value.Name != null)
        {
            writer.Line(frame + ".RegisterName(" + CSharpNames.Literal(value.Name) + ", " + variable + ");");
            if (_context.Document.ClassSymbol != null && _context.Document.Options.GenerateNamedFields && value.NameScopeId == _context.Document.Root!.NameScopeId)
                writer.Line(_context.RootVariable + "." + CSharpNames.Identifier(value.Name) + " = " + variable + ";");
        }
        if (value.SupportsInitialize) writer.Line("((global::System.ComponentModel.ISupportInitialize)" + variable + ").BeginInit();");
        if (value.UsableDuringInitialization) consume?.Invoke(variable);
        var collections = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        foreach (var assignment in value.Assignments)
            _context.Map(assignment.Span, () => _assignments.Emit(assignment, value, variable, frame, collections));
        if (value.SupportsInitialize) writer.Line("((global::System.ComponentModel.ISupportInitialize)" + variable + ").EndInit();");
        if (!value.UsableDuringInitialization) consume?.Invoke(variable);
        return variable;
    }
}
