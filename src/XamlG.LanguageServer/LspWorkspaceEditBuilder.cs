using System.Collections.Immutable;
using XamlG.Syntax;
using XamlG.Tooling.Refactoring;

namespace XamlG.LanguageServer;

/// <summary>Produces only text changes, addressed to the original URIs. Versions and source ranges
/// come from the same immutable snapshot used by refactoring, never from a later live buffer capture.</summary>
internal static class LspWorkspaceEditBuilder
{
    public static object Build(ImmutableArray<XamlDocumentEdits> edits, LspDocumentSetSnapshot snapshot, bool versioned)
    {
        var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var changed = edits.Select(edit =>
        {
            var xaml = snapshot.Documents.FirstOrDefault(d => paths.Equals(d.Syntax.Path, edit.Path));
            var csharp = snapshot.CSharpDocuments.FirstOrDefault(d => paths.Equals(d.Path, edit.Path));
            if (xaml != null && xaml.Syntax.Text != edit.OriginalText || csharp != null && csharp.Text.ToString() != edit.OriginalText)
                throw new LspRequestException(-32801, "The refactoring plan and open-buffer snapshot disagree.");
            var uri = xaml?.Uri ?? csharp?.Uri ?? LspConversions.UriForPath(edit.Path);
            int? version = xaml?.Version ?? csharp?.Version;
            return (Uri: uri, Version: version, Edits: TextEdits(edit));
        }).ToArray();
        if (versioned)
            return new { documentChanges = changed.Select(d => new { textDocument = new { uri = d.Uri, version = d.Version }, edits = d.Edits }).ToArray() };
        return new { changes = changed.ToDictionary(d => d.Uri, d => d.Edits, StringComparer.Ordinal) };
    }
    private static object[] TextEdits(XamlDocumentEdits document)
    {
        var lines = new SourceLineMap(document.OriginalText);
        return document.Changes.Select(edit =>
        {
            var start = lines.GetPosition(edit.Span.Start); var end = lines.GetPosition(edit.Span.End);
            return (object)new { range = new LspRange(new(start.Line, start.Character), new(end.Line, end.Character)), newText = edit.NewText };
        }).ToArray();
    }
}
