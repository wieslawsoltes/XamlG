using System.Collections.Immutable;
using System.Text.Json;
using XamlG.Tooling;
using XamlG.Tooling.Refactoring;

namespace XamlG.LanguageServer;

internal sealed class LspFileRenameRequests(XamlCompilationSession compiler, bool versionedEdits)
{
    public const string Method = "workspace/willRenameFiles";
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public object? Handle(JsonElement parameters, LspDocumentSetSnapshot buffers, ImmutableArray<XamlAnalysis> workspace, CancellationToken token)
    {
        if (!parameters.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > 256)
            throw new LspRequestException(-32602, "File renaming requires an array of at most 256 file or folder moves.");
        var requests = files.EnumerateArray().Select(item => (Old: FilePath(item, "oldUri"), New: FilePath(item, "newUri"))).ToArray();
        var moved = new List<XamlDocumentMove>();
        foreach (var document in compiler.ProjectDocuments)
        {
            token.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(document.Syntax.Path)) continue;
            var physical = Normalize(Path.GetFullPath(document.Syntax.Path));
            var applicable = requests.Where(r => Paths.Equals(r.Old, physical) || physical.StartsWith(r.Old.TrimEnd('/') + "/", Comparison)).ToArray();
            if (applicable.Length == 0) continue;
            if (applicable.Length != 1) throw new LspRequestException(-32602, "Overlapping file/folder moves are ambiguous for " + document.Syntax.Path);
            var operation = applicable[0];
            var nextPath = operation.New + physical.Substring(operation.Old.Length);
            var logical = Normalize(document.LogicalPath);
            var suffix = "/" + logical;
            if (!physical.EndsWith(suffix, Comparison))
                throw new LspRequestException(-32602, "Moving linked XAML files requires an explicit host logical-path mapping: " + document.Syntax.Path);
            var root = physical.Substring(0, physical.Length - logical.Length);
            if (!nextPath.StartsWith(root, Comparison))
                throw new LspRequestException(-32602, "The destination is outside this document's mapped project root.");
            moved.Add(new(document.Syntax.Path, nextPath, nextPath.Substring(root.Length)));
        }
        if (moved.Count == 0) return null;
        var plan = new XamlFileRenameService(compiler, Paths).Plan(moved, workspace, token);
        // Clients perform their own rename after applying these source edits. Returning another
        // RenameFile operation here would execute the move twice and violate willRenameFiles ordering.
        return plan.Documents.IsEmpty ? null : LspWorkspaceEditBuilder.Build(plan.Documents, buffers, versionedEdits);
    }
    private static string FilePath(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 8192 } text ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.IsFile || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new LspRequestException(-32602, "A file move requires absolute file URIs without query or fragment.");
        return Normalize(Path.GetFullPath(uri.LocalPath)).TrimEnd('/');
    }
    private static string Normalize(string path) => path.Replace('\\', '/');
}
