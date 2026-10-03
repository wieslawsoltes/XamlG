using System.Text.Json;
using XamlG.Syntax;

namespace XamlG.LanguageServer;

internal static class LspConversions
{
    public static LspPosition Position(JsonElement value) => new(value.GetProperty("line").GetInt32(), value.GetProperty("character").GetInt32());
    public static LspRange Range(JsonElement value) => new(Position(value.GetProperty("start")), Position(value.GetProperty("end")));
    public static LspRange Range(XamlSyntaxTree syntax, TextSpan span)
    {
        var start = syntax.Lines.GetPosition(Math.Min(span.Start, syntax.Text.Length));
        var end = syntax.Lines.GetPosition(Math.Min(span.End, syntax.Text.Length));
        return new(new(start.Line, start.Character), new(end.Line, end.Character));
    }
    public static string UriForPath(string path) => System.Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri ? uri.AbsoluteUri : new System.Uri(Path.GetFullPath(path)).AbsoluteUri;
    public static string DocumentUri(JsonElement parameters) => parameters.GetProperty("textDocument").GetProperty("uri").GetString() ?? throw new LspRequestException(-32602, "A textDocument URI is required.");
}
