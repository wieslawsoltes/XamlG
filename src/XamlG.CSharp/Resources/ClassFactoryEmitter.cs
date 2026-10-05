namespace XamlG.CSharp.Resources;

internal static class ClassFactoryEmitter
{
    public static void Emit(EmissionContext context, string typeName, string method)
    {
        var writer = context.Writer;
        var constructor = XamlClassFactory.Constructor(context.Document.ClassSymbol!)!;
        var initialize = ComponentInitializationEmitter.Method(context.Document.Options.DocumentId ?? context.Document.Syntax.Path);
        writer.Open("public static " + typeName + " " + method + "(" + CSharpNames.Provider + "? __services = null)");
        writer.Open("using (var __construction = global::XamlG.Runtime.XamlConstructionScope.Begin(typeof(" + typeName + "), __services))");
        writer.Open("try");
        writer.Line("var __root = new " + typeName + "(" + (constructor.Parameters.Length == 0 ? string.Empty : "__services") + ");");
        writer.Line(initialize + "(__root, __services);");
        writer.Line("__construction.Commit(__root);");
        writer.Line("return __root;");
        writer.Close();
        writer.Open("catch (global::System.Exception __constructionFailure)");
        writer.Line("__construction.Abort(__constructionFailure);");
        writer.Line("throw;");
        writer.Close(); writer.Close(); writer.Close();
    }
}
