using XamlG.Roslyn;

namespace XamlG.CSharp.Resources;

/// <summary>One component-specific initializer shared by generated constructors, handwritten
/// loader-call adapters and resource factories. State is per instance and per XAML document.</summary>
internal static class ComponentInitializationEmitter
{
    public static string Method(string documentId) => "__XamlGInitialize_" + CSharpNames.StableId(documentId);
    public static void Emit(EmissionContext context, string populate)
    {
        if (!context.Document.CanAugmentClass)
        { ExternalComponentInitializationEmitter.Emit(context, populate); return; }
        var type = context.Document.ClassSymbol!;
        var name = Method(context.Document.Options.DocumentId ?? context.Document.Syntax.Path);
        var state = "__xamlGInitializationState_" + context.Id;
        var writer = context.Writer;
        if (type.GetMembers(name).Any() || type.GetMembers(state).Any())
        { context.Error("The component defines a reserved XamlG initialization member.", context.Document.Root!.Syntax.NameSpan); return; }
        writer.Line("private byte " + state + ";");
        writer.Open("public static void " + name + "(" + type.CSharpName() + " instance, " + CSharpNames.Provider + "? services = null)");
        writer.Line("if (instance is null) throw new global::System.ArgumentNullException(nameof(instance));");
        writer.Line("if (instance." + state + " == 2) return;");
        writer.Line("if (instance." + state + " == 1) throw new global::System.InvalidOperationException(\"Reentrant XAML component initialization.\");");
        writer.Line("instance." + state + " = 1;");
        writer.Open("try");
        writer.Line(populate + "(instance, services ?? global::XamlG.Runtime.XamlConstructionScope.GetServicesFor(instance));");
        writer.Line("global::XamlG.Runtime.XamlConstructionScope.RegisterInitialized(typeof(" + type.CSharpName() + "), instance);");
        writer.Line("instance." + state + " = 2;");
        writer.Close();
        writer.Open("catch"); writer.Line("instance." + state + " = 0;"); writer.Line("throw;"); writer.Close();
        writer.Close();
        if (!type.GetMembers("InitializeComponent").Any())
        {
            writer.Open("private void InitializeComponent()");
            writer.Line(name + "(this);");
            writer.Close();
        }
    }
}
