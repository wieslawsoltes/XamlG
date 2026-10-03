using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

internal sealed class CallAssignmentEmitter(EmissionContext context, ValueEmitter values)
{
    public void Emit(BoundCallAssignment assignment, string target, string frame)
    {
        var writer = context.Writer;
        if (assignment.TargetDescriptor != null)
        {
            var local = context.Temporary("callTarget");
            writer.Line("var " + local + " = " + frame + ".ForTarget(" + target + ", " +
                values.Emit(assignment.TargetDescriptor, frame) + ");");
            frame = local;
        }
        var arguments = assignment.Arguments.Select(argument => values.Emit(argument, frame)).ToList();
        if (assignment.IncludeTarget) arguments.Insert(0, target);
        var call = (assignment.Method.IsStatic ? assignment.Method.ContainingType.CSharpName() : target) +
            "." + CSharpNames.Method(assignment.Method) + "(" + string.Join(", ", arguments) + ")";
        if (!assignment.OwnResult) { writer.Line(call + ";"); return; }
        var resultType = assignment.Method.ReturnType;
        if (!resultType.HasMetadataName("System.IDisposable") &&
            !resultType.AllInterfaces.Any(type => type.HasMetadataName("System.IDisposable")))
            context.Error("An owned method assignment must return IDisposable.", assignment.Span);
        var subscription = context.Temporary("ownedCall");
        writer.Line("global::System.IDisposable? " + subscription + " = " + call + ";");
        writer.Line(frame + ".Session.TrackCleanup(() => " + subscription + "?.Dispose());");
    }
}
