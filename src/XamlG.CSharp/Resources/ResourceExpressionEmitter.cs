using XamlG.Compiler.Resources;
using XamlG.Roslyn;

namespace XamlG.CSharp.Resources;

internal sealed class ResourceExpressionEmitter(EmissionContext context)
{
    public string Emit(BoundResourceExpression expression, string frame)
    {
        var resource = expression.Resource;
        var target = resource.ExternalFactory is { } external
            ? external.ContainingType.CSharpName() + "." + CSharpNames.Method(external)
            : resource.LocalFactoryType is { } local
                ? local.CSharpName() + "." + CSharpNames.Identifier(resource.LocalFactoryMethod!)
                : "global::" + resource.GeneratedNamespace + ".Document_" + CSharpNames.StableId(resource.LocalDocumentId!) + ".Build";
        var value = context.Temporary("resource");
        var session = context.Temporary("resourceSession");
        context.Writer.Line("var " + value + " = " + target + "(global::XamlG.Runtime.XamlResourceServices.Enter(" + frame + ", " + CSharpNames.Literal(resource.Uri) + "));");
        context.Writer.Line("if (global::XamlG.Runtime.XamlRuntimeSession.TryGet(" + value + ", out var " + session + ") && !global::System.Object.ReferenceEquals(" + session + ", " + frame + ".Session)) " + frame + ".Session.TrackCleanup(" + session + "!.Dispose);");
        return value;
    }
}
