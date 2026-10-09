using XamlG.Compiler;
using XamlG.Roslyn;
using Microsoft.CodeAnalysis;

namespace XamlG.CSharp;

internal sealed class CallAssignmentEmitter(EmissionContext context, ValueEmitter values)
{
    public void Emit(BoundCallAssignment assignment, ITypeSymbol ownerType, string target, string frame)
    {
        var writer = context.Writer;
        if (assignment.TargetMember is { } member)
        {
            var receiver = context.Locals.Declare(member.Getter!.ReturnType.CSharpName(), AssignmentEmitter.Get(member, ownerType, target), "callReceiver", inferred: true);
            target = receiver;
        }
        if (assignment.TargetDescriptor != null)
        {
            var local = context.Locals.Declare(CSharpNames.Context, frame + ".ForTarget(" + target + ", " +
                values.Emit(assignment.TargetDescriptor, frame) + ")", "callTarget", inferred: true);
            context.InheritFrameNamespaces(local, frame);
            frame = local;
        }
        var arguments = new List<string>();
        for (var index = 0; index < assignment.Arguments.Length; index++)
        {
            var parameterIndex = index + (assignment.IncludeTarget ? 1 : 0);
            var expression = index == assignment.Arguments.Length - 1
                ? values.EmitInitialized(assignment.Arguments[index], frame, assignment.ValueInitializers, arguments)
                : values.Emit(assignment.Arguments[index], frame);
            var local = context.Locals.Declare(assignment.Method.Parameters[parameterIndex].Type.CSharpName(), expression, "argument");
            arguments.Add(local);
        }
        var inputs = assignment.IncludeTarget ? new[] { target }.Concat(arguments) : arguments;
        var call = (assignment.Method.IsStatic ? assignment.Method.ContainingType.CSharpName() : target) +
            "." + CSharpNames.Method(assignment.Method) + "(" + string.Join(", ", inputs) + ")";
        if (!assignment.OwnResult)
        {
            writer.Line(call + ";");
            new PostCallEmitter(context, values).Emit(assignment.PostCall, target, arguments, frame, assignment.Span);
            return;
        }
        var resultType = assignment.Method.ReturnType;
        if (!resultType.HasMetadataName("System.IDisposable") &&
            !resultType.AllInterfaces.Any(type => type.HasMetadataName("System.IDisposable")))
            context.Error("An owned method assignment must return IDisposable.", assignment.Span);
        var subscription = context.Temporary("ownedCall");
        writer.Line("global::System.IDisposable? " + subscription + " = " + call + ";");
        writer.Line(frame + ".Session.TrackDisposable(" + subscription + ");");
        new PostCallEmitter(context, values).Emit(assignment.PostCall, target, arguments, frame, assignment.Span);
    }
}
