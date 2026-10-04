using System.Text.Json;

namespace XamlG.LanguageServer;

internal sealed record LspClientFeatures(bool VersionedEdits, bool PullDiagnostics,
    bool RelatedDiagnostics, bool DiagnosticRefresh, bool WillRenameFiles)
{
    public static LspClientFeatures Read(JsonElement initialize)
    {
        bool Flag(params string[] path) => Find(initialize, path).ValueKind == JsonValueKind.True;
        return new(Flag("capabilities", "workspace", "workspaceEdit", "documentChanges"),
            Find(initialize, "capabilities", "textDocument", "diagnostic").ValueKind == JsonValueKind.Object,
            Flag("capabilities", "textDocument", "diagnostic", "relatedDocumentSupport"),
            Flag("capabilities", "workspace", "diagnostics", "refreshSupport"),
            Flag("capabilities", "workspace", "fileOperations", "willRename"));
    }
    private static JsonElement Find(JsonElement value, params string[] path)
    {
        foreach (var part in path)
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) return default;
        return value;
    }
}
