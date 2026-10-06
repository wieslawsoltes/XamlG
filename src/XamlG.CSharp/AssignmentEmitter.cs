using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
namespace XamlG.CSharp;
internal sealed class AssignmentEmitter
{
    private readonly EmissionContext _context;
    private readonly ObjectEmitter _objects;
    private readonly ValueEmitter _values;
    public AssignmentEmitter(EmissionContext context, ObjectEmitter objects, ValueEmitter values) { _context = context; _objects = objects; _values = values; }
    public void Emit(BoundAssignment assignment, BoundObject owner, string target, string frame, Dictionary<ISymbol, string> collections)
    {
        if (BoundTraversal.Expressions(assignment).Any(BoundTraversal.ContainsReference))
        { _context.Writer.Open(frame + ".Defer(() =>"); EmitCore(assignment, owner, target, frame, new(SymbolEqualityComparer.Default)); _context.Writer.Close(");"); }
        else EmitCore(assignment, owner, target, frame, collections);
    }
    private void EmitCore(BoundAssignment assignment, BoundObject owner, string target, string frame, Dictionary<ISymbol, string> collections)
    {
        var writer = _context.Writer;
        switch (assignment)
        {
            case BoundSetAssignment set:
            {
                collections.Remove(set.Member.Symbol); var valueFrame = ForTarget(set.Member, target, frame);
                void Assign(string value)
                {
                    Set(set.Member, target, value);
                    if (set.Member.Getter != null && set.Member.Setter != null && !set.Member.Setter.IsInitOnly)
                        writer.Line(frame + ".Session.RegisterProperty<" + set.Member.ValueType.CSharpName() + ">(" + CSharpNames.Literal(owner.Key) + ", " + CSharpNames.Literal(set.Member.Name) + ", () => " + Get(set.Member, target) + ", __value => { " + SetExpression(set.Member, target, "__value") + "; });");
                }
                if (set.Value is BoundObjectExpression child) _objects.Emit(child.Object, valueFrame, null, Assign); else Assign(_values.Emit(set.Value, valueFrame));
                break;
            }
            case BoundAddAssignment add:
            {
                var receiver = target; var valueFrame = frame;
                if (add.Collection != null)
                {
                    valueFrame = ForTarget(add.Collection, target, frame);
                    if (!collections.TryGetValue(add.Collection.Symbol, out receiver!))
                    { receiver = _context.Temporary("collection"); writer.Line("var " + receiver + " = " + Get(add.Collection, target) + ";"); collections.Add(add.Collection.Symbol, receiver); }
                }
                var arguments = new List<string>();
                for (var i = 0; i < add.Arguments.Length - 1; i++)
                {
                    var argument = _context.Temporary("argument");
                    writer.Line(add.AddMethod.Parameters[i].Type.CSharpName() + " " + argument + " = " + _values.Emit(add.Arguments[i], valueFrame) + ";");
                    arguments.Add(argument);
                }
                var last = add.Arguments[add.Arguments.Length - 1];
                void Add(string value) => writer.Line("((" + add.AddMethod.ContainingType.CSharpName() + ")" + receiver + ")." + CSharpNames.Method(add.AddMethod) + "(" + string.Join(", ", arguments.Concat(new[] { value })) + ");");
                if (last is BoundObjectExpression child)
                    _objects.Emit(child.Object, valueFrame, null, Add,
                        value => ArgumentInitializerEmitter.Emit(_context, value, add.ValueInitializers, arguments));
                else Add(_values.EmitInitialized(last, valueFrame, add.ValueInitializers, arguments));
                break;
            }
            case BoundEventAssignment ev:
            {
                var handler = _context.Temporary("handler"); writer.Line(ev.Event.ValueType.CSharpName() + " " + handler + " = " + _context.RootVariable + "." + CSharpNames.Identifier(ev.HandlerName) + ";");
                if (ev.Event.Kind == BoundMemberKind.Event)
                {
                    writer.Line(target + "." + CSharpNames.Identifier(ev.Event.Name) + " += " + handler + ";");
                    writer.Line(frame + ".Session.TrackCleanup(() => " + target + "." + CSharpNames.Identifier(ev.Event.Name) + " -= " + handler + ");");
                }
                else
                {
                    var method = ev.Event.Setter!; writer.Line(method.ContainingType.CSharpName() + "." + CSharpNames.Method(method) + "(" + target + ", " + handler + ");");
                    var remove = method.ContainingType.GetMembers("Remove" + ev.Event.Name + "Handler").OfType<IMethodSymbol>().FirstOrDefault(m => m.IsStatic && m.Parameters.Length == 2);
                    if (remove != null) writer.Line(frame + ".Session.TrackCleanup(() => " + remove.ContainingType.CSharpName() + "." + CSharpNames.Method(remove) + "(" + target + ", " + handler + "));");
                }
                break;
            }
            case BoundCallAssignment call:
                new CallAssignmentEmitter(_context, _values).Emit(call, target, frame);
                break;
            case BoundDynamicSetAssignment dynamicSet: Dynamic(dynamicSet, owner.Type, target, frame); break;
            case BoundAdaptedSetAssignment adapted:
                new AdaptedAssignmentEmitter(_context, _values).Emit(adapted, target, frame,
                    ForTarget(adapted.Member, target, frame), Set);
                break;
            case BoundRawAssignment raw: writer.Line(_values.ExpandTrusted(raw.CSharp, frame, target)); break;
            default: _context.Error("The backend does not recognize assignment '" + assignment.GetType().Name + "'.", assignment.Span); break;
        }
    }
    private void Dynamic(BoundDynamicSetAssignment assignment, INamedTypeSymbol ownerType, string target, string frame)
    {
        var value = _values.Emit(assignment.Value, ForTarget(assignment.Target, target, frame));
        var helper = _context.DynamicSetters.Register(ownerType, assignment.Candidates);
        _context.Writer.Line(helper + "(" + target + ", " + value + ");");
    }

    private string ForTarget(BoundMember member, string target, string parent)
    {
        var descriptor = member.TargetDescriptor == null ? _context.Descriptor(member) : _values.Emit(member.TargetDescriptor, parent);
        var frame = _context.Temporary("target"); _context.Writer.Line("var " + frame + " = " + parent + ".ForTarget(" + target + ", " + descriptor + ");"); return frame;
    }
    private static string Get(BoundMember member, string target) => member.Kind == BoundMemberKind.AttachedProperty ? member.Getter!.ContainingType.CSharpName() + "." + CSharpNames.Method(member.Getter) + "(" + target + ")" : target + "." + CSharpNames.Identifier(member.Name);
    private string SetExpression(BoundMember member, string target, string value)
    {
        if (member.Setter?.IsInitOnly == true) return _context.InitSetter(member.Setter) + "(" + (member.Setter.ContainingType.IsValueType ? "ref " : string.Empty) + target + ", " + value + ")";
        return member.Kind == BoundMemberKind.AttachedProperty ? member.Setter!.ContainingType.CSharpName() + "." + CSharpNames.Method(member.Setter) + "(" + target + ", " + value + ")" : target + "." + CSharpNames.Identifier(member.Name) + " = " + value;
    }
    private void Set(BoundMember member, string target, string value) => _context.Writer.Line(SetExpression(member, target, value) + ";");
}
