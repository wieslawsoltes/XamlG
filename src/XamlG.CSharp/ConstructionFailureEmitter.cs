namespace XamlG.CSharp;

internal static class ConstructionFailureEmitter
{
    public static void Emit(EmissionContext context, string frame)
    {
        var writer = context.Writer;
        writer.Open("catch (global::System.Exception __constructionFailure)");
        writer.Line(frame + ".Session.DisposeAfterConstructionFailure(__constructionFailure);");
        writer.Line("throw;");
        writer.Close();
    }

}
