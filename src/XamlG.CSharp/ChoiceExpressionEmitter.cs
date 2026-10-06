using XamlG.Compiler;
using XamlG.Roslyn;
namespace XamlG.CSharp;

internal sealed class ChoiceExpressionEmitter(EmissionContext context, ObjectEmitter objects, ValueEmitter values)
{
    public string Emit(BoundChoiceExpression choice, string frame)
    {
        var receiver = objects.Emit(choice.Extension, frame, null, null);
        var result = context.Temporary("choice");
        context.Writer.Line(choice.ResultType.CSharpName() + " " + result + ";");
        context.Writer.Open("do");
        foreach (var branch in choice.Branches)
        {
            var predicate = branch.Predicate;
            var arguments = (predicate.Parameters.Length == 2 ? frame + ", " : string.Empty) + values.Emit(branch.Option, frame);
            var call = (predicate.IsStatic ? predicate.ContainingType.CSharpName() : receiver) + "." + CSharpNames.Method(predicate) + "(" + arguments + ")";
            context.Writer.Open("if (" + call + ")");
            context.Writer.Line(result + " = " + values.Emit(branch.Value, frame) + ";");
            context.Writer.Line("break;");
            context.Writer.Close();
        }
        context.Writer.Line(result + " = " + (choice.Default == null ? "default!" : values.Emit(choice.Default, frame)) + ";");
        context.Writer.Close(" while (false);");
        return result;
    }
}
