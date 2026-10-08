using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

internal sealed class AdaptedAssignmentEmitter(EmissionContext context, ValueEmitter values)
{
    public void Emit(BoundAdaptedSetAssignment assignment, string target, string frame, string valueFrame,
        Action<BoundMember, string, string> set)
    {
        var writer = context.Writer;
        var value = context.Temporary("adapted");
        writer.Line("object? " + value + " = " + values.Emit(assignment.Value, valueFrame) + ";");
        var first = true;
        foreach (var type in assignment.AdaptedTypes)
        {
            var typed = context.Temporary("value");
            writer.Open((first ? "if" : "else if") + " (" + value + " is " + type.CSharpName() + " " + typed + ")");
            var arguments = new List<string> { target };
            if (assignment.Adapter.Parameters.Length == 3)
                arguments.Add(assignment.Member.TargetDescriptor == null ? context.Descriptor(assignment.Member) : values.Emit(assignment.Member.TargetDescriptor, frame));
            arguments.Add(typed);
            var call = assignment.Adapter.ContainingType.CSharpName() + "." + CSharpNames.Method(assignment.Adapter) + "(" + string.Join(", ", arguments) + ")";
            if (assignment.OwnAdapterResult)
            {
                var resultType = assignment.Adapter.ReturnType;
                if (!resultType.HasMetadataName("System.IDisposable") && !resultType.AllInterfaces.Any(i => i.HasMetadataName("System.IDisposable")))
                    context.Error("An owned adapter result must implement IDisposable.", assignment.Span);
                var subscription = context.Temporary("subscription");
                writer.Line("global::System.IDisposable? " + subscription + " = " + call + ";");
                writer.Line(frame + ".Session.TrackDisposable(" + subscription + ");");
            }
            else writer.Line(call + ";");
            writer.Close();
            first = false;
        }
        writer.Open(first ? string.Empty : "else");
        set(assignment.Member, target, "(" + assignment.Member.ValueType.CSharpName() + ")" + value + "!");
        writer.Close();
    }
}
