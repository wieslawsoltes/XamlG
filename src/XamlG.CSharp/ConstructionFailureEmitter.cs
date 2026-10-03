namespace XamlG.CSharp;

internal static class ConstructionFailureEmitter
{
    public static void Emit(EmissionContext context, string frame)
    {
        var writer = context.Writer;
        writer.Open("catch (global::System.Exception __constructionFailure)");
        writer.Open("try");
        writer.Line(frame + ".Session.Dispose();");
        writer.Close();
        writer.Open("catch (global::System.Exception __cleanupFailure)");
        writer.Line("throw new global::System.AggregateException(\"XAML construction and cleanup both failed.\", __constructionFailure, __cleanupFailure);");
        writer.Close();
        writer.Line("throw;");
        writer.Close();
    }

}
