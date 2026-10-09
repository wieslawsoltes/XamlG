using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
namespace XamlG.CSharp;
internal sealed class AssignmentEmitter
{
    private readonly EmissionContext _context;
    private readonly ObjectEmitter _objects;
    private readonly ValueEmitter _values;
    private readonly LiteralAssignmentEmitter _literals;
    public AssignmentEmitter(EmissionContext context, ObjectEmitter objects, ValueEmitter values)
    { _context = context; _objects = objects; _values = values; _literals = new(context, values); }
    public void EmitHelpers() => _literals.EmitHelpers();
    public void Emit(BoundAssignment assignment, BoundObject owner, string target, string frame)
    {
        using var temporaries = _context.Locals.EnterAssignment(owner, assignment);
        if (_context.Locals.ContainsReference(assignment))
        { _context.Writer.Open(frame + ".Defer(() =>"); EmitCore(assignment, owner, target, frame); _context.Writer.Close(");"); }
        else EmitCore(assignment, owner, target, frame);
    }
    private void EmitCore(BoundAssignment assignment, BoundObject owner, string target, string frame)
    {
        var writer = _context.Writer;
        switch (assignment)
        {
            case BoundSetAssignment set:
            {
                if (_literals.TryEmit(set, owner, target, frame)) break;
                var valueFrame = ForTarget(set.Member, target, frame, ValueEmitter.UsesFrame(set.Value));
                void Assign(string value)
                {
                    if (PropertyAssignmentEmitter.TryEmit(_context, set, owner, target, frame, value)) return;
                    if (set.RegisterName)
                    {
                        var name = _context.Locals.Declare("string", "((string)(" + value + "))!", "name");
                        value = name;
                    }
                    Set(set.Member, owner.Type, target, value);
                    if (set.RegisterName) _objects.RegisterName(frame, value, target);
                    if (set.Member.Getter != null && set.Member.Setter != null && !set.Member.Setter.IsInitOnly)
                    {
                        if (owner.Type.IsReferenceType)
                        {
                            writer.Line(_context.PropertyRegistration(PropertyAccessor.Create(owner.Type, set.Member), frame));
                        }
                        else
                            writer.Line(frame + ".Session.RegisterProperty<" + set.Member.ValueType.CSharpName() + ">(" + CSharpNames.Literal(owner.Key) + ", " + CSharpNames.Literal(set.Member.Name) + ", () => " + Get(set.Member, owner.Type, target) + ", __value => { " + SetExpression(set.Member, owner.Type, target, "__value") + "; });");
                    }
                }
                if (set.Value is BoundObjectExpression child) _objects.Emit(child.Object, valueFrame, null, Assign); else Assign(_values.Emit(set.Value, valueFrame));
                break;
            }
            case BoundAddAssignment add:
            {
                if (add.PostCall != null && !add.Alternatives.IsDefaultOrEmpty)
                { _context.Error("A collection post-call requires a statically selected adder.", add.Span); break; }
                var receiver = target; var valueFrame = frame;
                if (add.Collection != null)
                {
                    valueFrame = ForTarget(add.Collection, target, frame);
                    if (add.Alternatives.IsDefaultOrEmpty)
                    { receiver = _context.Locals.Declare(add.Collection.Getter!.ReturnType.CSharpName(), Get(add.Collection, owner.Type, target), "collection", inferred: true); }
                }
                var arguments = new List<string>();
                for (var i = 0; i < add.Arguments.Length - 1; i++)
                {
                    var argument = _context.Locals.Declare(add.AddMethod.Parameters[i].Type.CSharpName(), _values.Emit(add.Arguments[i], valueFrame), "argument");
                    arguments.Add(argument);
                }
                var last = add.Arguments[add.Arguments.Length - 1];
                void Add(string value)
                {
                    if (add.PostCall != null)
                    {
                        var local = _context.Locals.Declare(add.AddMethod.Parameters[add.Arguments.Length - 1].Type.CSharpName(), value, "item");
                        value = local;
                    }
                    var inputs = string.Join(", ", arguments.Concat(new[] { value }).Select((input, index) =>
                        add.Alternatives.IsDefaultOrEmpty ? "(" + add.AddMethod.Parameters[index].Type.CSharpName() + ")(" + input + ")" : input));
                    if (add.Alternatives.IsDefaultOrEmpty)
                        writer.Line("((" + add.AddMethod.ContainingType.CSharpName() + ")" + receiver + ")." + CSharpNames.Method(add.AddMethod) + "(" + inputs + ");");
                    else
                        writer.Line(_context.DynamicAdds.Register(owner.Type, add.Collection, add.Alternatives) + "(" + target + ", " + inputs + ");");
                    new PostCallEmitter(_context, _values).Emit(add.PostCall, receiver, arguments.Concat(new[] { value }).ToArray(), valueFrame, add.Span);
                }
                if (last is BoundObjectExpression child)
                    _objects.Emit(child.Object, valueFrame, null, Add,
                        value => ArgumentInitializerEmitter.Emit(_context, value, add.ValueInitializers, arguments));
                else Add(_values.EmitInitialized(last, valueFrame, add.ValueInitializers, arguments));
                break;
            }
            case BoundEventAssignment ev:
            {
                var receiver = ev.Handler == null ? _context.RootVariable : "((" + ev.Handler.ContainingType.CSharpName() + ")" + _context.RootVariable + ")";
                var value = ev.Value == null ? receiver + "." + CSharpNames.Identifier(ev.HandlerName) : _values.Emit(ev.Value, ForTarget(ev.Event, target, frame));
                var handler = _context.Temporary("handler"); writer.Line(ev.Event.ValueType.CSharpName() + " " + handler + " = " + value + ";");
                if (ev.Event.Kind == BoundMemberKind.Event)
                {
                    var eventTarget = CSharpNames.MemberTarget(ev.Event.Symbol, owner.Type, target);
                    writer.Line(eventTarget + "." + CSharpNames.Identifier(ev.Event.Name) + " += " + handler + ";");
                    writer.Line(frame + ".Session.TrackCleanup(() => " + eventTarget + "." + CSharpNames.Identifier(ev.Event.Name) + " -= " + handler + ");");
                }
                else
                {
                    var method = ev.Event.Setter!; writer.Line(method.ContainingType.CSharpName() + "." + CSharpNames.Method(method) + "(" + target + ", " + handler + ");");
                    var remove = method.ContainingType.GetMembers("Remove" + ev.Event.Name + "Handler").OfType<IMethodSymbol>().FirstOrDefault(m => m.IsStatic && m.Parameters.Length == 2 &&
                        m.Parameters.Select((parameter, index) => parameter.RefKind == method.Parameters[index].RefKind && SymbolEqualityComparer.Default.Equals(parameter.Type, method.Parameters[index].Type)).All(value => value));
                    if (remove != null) writer.Line(frame + ".Session.TrackCleanup(() => " + remove.ContainingType.CSharpName() + "." + CSharpNames.Method(remove) + "(" + target + ", " + handler + "));");
                }
                break;
            }
            case BoundCallAssignment call:
                new CallAssignmentEmitter(_context, _values).Emit(call, owner.Type, target, frame);
                break;
            case BoundDynamicSetAssignment dynamicSet: Dynamic(dynamicSet, owner.Type, target, frame); break;
            case BoundAdaptedSetAssignment adapted:
                new AdaptedAssignmentEmitter(_context, _values).Emit(adapted, target, frame,
                    ForTarget(adapted.Member, target, frame), (member, receiver, value) => Set(member, owner.Type, receiver, value));
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

    private string ForTarget(BoundMember member, string target, string parent, bool required = true)
    {
        if (!required)
        {
            // A framework descriptor may execute user code even when the value does not
            // consume target services. Retain its evaluation before evaluating the value.
            if (member.TargetDescriptor is { } expression)
                _context.Locals.Declare("object?", _values.Emit(expression, parent), "descriptorValue");
            return parent;
        }
        var descriptor = member.TargetDescriptor == null ? _context.Descriptor(member) : _values.Emit(member.TargetDescriptor, parent);
        var frame = _context.Locals.Declare(CSharpNames.Context, parent + ".ForTarget(" + target + ", " + descriptor + ")", "target", inferred: true);
        _context.InheritFrameNamespaces(frame, parent); return frame;
    }
    internal static string Get(BoundMember member, ITypeSymbol targetType, string target) => member.Kind == BoundMemberKind.AttachedProperty ? member.Getter!.ContainingType.CSharpName() + "." + CSharpNames.Method(member.Getter) + "(" + target + ")" : CSharpNames.MemberTarget(member.Symbol, targetType, target) + "." + CSharpNames.Identifier(member.Name);
    private string SetExpression(BoundMember member, ITypeSymbol targetType, string target, string value)
    {
        if (member.StaticSetter == null && member.Setter?.IsInitOnly == true)
            return _context.InitSetter(member.Setter) + "(" + (member.Setter.ContainingType.IsValueType ? "ref " : string.Empty) + target + ", " + value + ")";
        return SetNonInit(member, targetType, target, value);
    }
    internal static string SetNonInit(BoundMember member, ITypeSymbol targetType, string target, string value)
    {
        if (member.StaticSetter is { } accessor)
        {
            var arguments = new[] { target }.Concat(accessor.Descriptors.Select(field => field.Member.ContainingType.CSharpName() + "." + CSharpNames.Identifier(field.GeneratedMemberName ?? field.Member.Name))).Concat(new[] { value });
            return accessor.Method.ContainingType.CSharpName() + "." + CSharpNames.Method(accessor.Method) + "(" + string.Join(", ", arguments) + ")";
        }
        return member.Kind == BoundMemberKind.AttachedProperty ? member.Setter!.ContainingType.CSharpName() + "." + CSharpNames.Method(member.Setter) + "(" + target + ", " + value + ")" : CSharpNames.MemberTarget(member.Symbol, targetType, target) + "." + CSharpNames.Identifier(member.Name) + " = " + value;
    }
    private void Set(BoundMember member, ITypeSymbol targetType, string target, string value) => _context.Writer.Line(SetExpression(member, targetType, target, value) + ";");
}
