using XamlG.Roslyn;

namespace XamlG.CSharp.Resources;

/// <summary>Keeps initialization ownership outside a non-partial component without rooting instances.</summary>
internal static class ExternalComponentInitializationEmitter
{
    public static void Emit(EmissionContext context, string populate)
    {
        var type = context.Document.ClassSymbol!.CSharpName();
        var name = ComponentInitializationEmitter.Method(context.Document.Options.DocumentId ?? context.Document.Syntax.Path);
        var state = "__xamlGInitializationStates_" + context.Id;
        var writer = context.Writer;
        var table = "global::System.Runtime.CompilerServices.ConditionalWeakTable<" + type + ", global::XamlG.Runtime.XamlComponentInitializationState>";
        writer.Line("private static readonly " + table + " " + state + " = new " + table + "();");
        writer.Open("public static void " + name + "(" + type + " instance, " + CSharpNames.Provider + "? services = null)");
        writer.Line("if (instance is null) throw new global::System.ArgumentNullException(nameof(instance));");
        writer.Line(state + ".GetValue(instance, static _ => new global::XamlG.Runtime.XamlComponentInitializationState()).Initialize(instance, services, " + populate + ");");
        writer.Close();
    }
}
