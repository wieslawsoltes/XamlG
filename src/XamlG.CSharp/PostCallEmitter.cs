using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.CSharp;

internal sealed class PostCallEmitter(EmissionContext context, ValueEmitter values)
{
    public void Emit(BoundPostCall? call, string target, IReadOnlyList<string> arguments, string frame, TextSpan span)
    {
        if (call == null) return;
        if (!call.Method.IsStatic || call.Method.Parameters.Length != 1 + call.ArgumentIndices.Length + call.Arguments.Length ||
            call.ArgumentIndices.Any(index => index < 0 || index >= arguments.Count))
        { context.Error("An assignment post-call must select valid arguments and match a static method.", span); return; }
        var inputs = new[] { target }.Concat(call.ArgumentIndices.Select(index => arguments[index]))
            .Concat(call.Arguments.Select(argument => values.Emit(argument, frame)));
        var typed = inputs.Select((input, index) => "(" + call.Method.Parameters[index].Type.CSharpName() + ")(" + input + ")");
        context.Writer.Line(call.Method.ContainingType.CSharpName() + "." + CSharpNames.Method(call.Method) + "(" + string.Join(", ", typed) + ");");
    }
}
