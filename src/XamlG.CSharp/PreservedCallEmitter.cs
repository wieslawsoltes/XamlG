using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

/// <summary>Keeps a library's statically selected dispatch methods reachable by the trimmer.</summary>
internal sealed class PreservedCallEmitter(EmissionContext context, ValueEmitter values)
{
    public string Emit(BoundCallExpression call, string frame)
    {
        if (!call.Method.IsStatic || call.Method.Parameters.Any(parameter => parameter.RefKind != RefKind.None))
        {
            context.Error("A preserved runtime dispatch requires a static method with value parameters.", call.Span);
            return "default!";
        }
        var dependency = call.RuntimeDependency!;
        var name = context.Temporary("preservedCall");
        var arguments = call.Method.Parameters.Select((parameter, index) => "__argument" + index).ToArray();
        var writer = context.Writer;
        writer.Line("[global::System.Diagnostics.CodeAnalysis.DynamicDependency(global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods, " +
            CSharpNames.Literal(dependency.MetadataName) + ", " + CSharpNames.Literal(dependency.AssemblyName) + ")]");
        writer.Line("[global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(\"Trimming\", \"IL2026\", Justification = \"The statically selected compiled resource loader is preserved by DynamicDependency.\")]");
        writer.Open("static " + call.Method.ReturnType.CSharpName() + " " + name + "(" + string.Join(", ",
            call.Method.Parameters.Select((parameter, index) => parameter.Type.CSharpName() + " " + arguments[index])) + ")");
        writer.Line("return " + call.Method.ContainingType.CSharpName() + "." + CSharpNames.Method(call.Method) + "(" + string.Join(", ", arguments) + ");");
        writer.Close();
        return name + "(" + string.Join(", ", values.EmitArguments(call.Method, call.Arguments, frame)) + ")";
    }
}
