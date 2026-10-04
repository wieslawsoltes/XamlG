# Source-aware authoring APIs

These APIs operate on immutable compiler snapshots. They return source edits, not mutated runtime objects or file-system changes. Use the same evaluated Roslyn compilation, framework profile and project logical paths as build-time compilation.

## Scoped name identity

`XamlNameReferenceIndex.Create(analysis, session)` builds declarations and references using actual `BoundObject.NameScopeId` values. Deferred/template scopes remain separate even when names are spelled identically. Framework profiles register `IXamlNameReferenceRule`; the portable profile recognizes `x:Reference`, and the Avalonia profile adds binding `ElementName` and `#name` paths.

Precise markup-argument value spans are exposed through the nullable `MarkupArgumentSyntax.ValueSpan` property. `XamlDecodedTextMap` maps decoded UTF-16 boundaries back to raw XML, including numeric entities and supplementary scalars. `XamlMarkupScanner` walks nested markup without searching arbitrary literal strings. It rejects ambiguous/malformed mappings rather than returning unsafe edit ranges. Existing positional record constructors remain compatible.

Compiler-ignored markup compatibility/design namespaces are excluded from rename. Ordinary strings, comments, unknown extension conventions, `FindControl("name")` strings and style-selector strings are not renamed automatically.

## Rename a name and code-behind field references

```csharp
using System.Threading;
using XamlG.Syntax;
using XamlG.Tooling;
using XamlG.Tooling.Refactoring;

public static class RenameCommand
{
    public static XamlRenamePlan Create(
        XamlCompilationSession session,
        XamlSyntaxTree[] openBuffers,
        XamlSyntaxTree selected,
        int utf16Offset,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var workspace = session.AnalyzeWorkspace(openBuffers, cancellationToken);
        XamlAnalysis? owner = null;
        foreach (var analysis in workspace)
            if (ReferenceEquals(analysis.Syntax, selected)) owner = analysis;
        if (owner is null)
            throw new System.ArgumentException("The selected snapshot is not part of the workspace.");
        return new XamlRenameService(session).Rename(
            owner, utf16Offset, newName, workspace, cancellationToken);
    }
}
```

The returned plan contains the old/new names, selection span and per-document edits with original text and versions. Apply all edits as one host transaction only if the source snapshots still match. Edits use original UTF-16 coordinates and are ordered/non-overlapping. Do not update XAML while dropping required C# edits.

For root names in code-behind classes, the service augments the Roslyn compilation with generated output and resolves the actual generated or explicit field. It changes field declarations/usages and `nameof` references in loaded original C# syntax trees, not comments, string literals or same-spelled unrelated locals. It rejects invalid/keyword identifiers, duplicate names in the same namescope, inherited/member collisions and unqualified C# references that would capture locals/parameters after rename. General type/member rename and arbitrary C# refactoring are outside this service.

`XamlCSharpReferenceService` exposes source-only symbol reference lookup, including code-behind field uses. The host owns refreshing unsaved C# buffers into its Roslyn compilation; the XAML LSP buffer store is not a C# language server.

## Formatting without XML serialization

```csharp
using System.Threading;
using XamlG.Tooling;
using XamlG.Tooling.Formatting;
using XamlG.Syntax;
using System.Collections.Immutable;

public static class FormatCommand
{
    public static ImmutableArray<XamlTextChange> Create(
        XamlAnalysis analysis, TextSpan? selection = null,
        CancellationToken cancellationToken = default) =>
        XamlFormatter.Format(analysis.Syntax,
            new XamlFormattingOptions { TabSize = 2, InsertSpaces = true },
            selection, analysis, cancellationToken);
}
```

Formatting changes indentation and syntactic whitespace; it does not normalize values or reserialize XML. It preserves attributes' raw entity/quote spelling, mixed literal content, CDATA, comments and `xml:space`. With a matching semantic analysis it also protects framework-declared whitespace-significant collection content, including property elements. A stale analysis is rejected. Malformed syntax returns no edits.

Options support spaces/tabs, indentation, self-closing spacing, attribute splitting/line width and final newline insertion. Range formatting emits only edits wholly inside the requested source range. Reformatting the result is idempotent. Supplying only syntax cannot discover arbitrary framework attributes on collection types; semantic-aware hosts should pass `XamlAnalysis`.

## Structural actions and resource navigation

`XamlCodeActionService` returns source-preserving expansion of self-closing tags, collapse of truly empty elements, formatting, and a unique member-name spelling correction among accessible writable properties/events. It does not remove comments or whitespace-bearing content merely to make an element self-closing.

`XamlResourceLanguageService` inspects typed resource expressions, reports canonical URI/root type, locates local source targets and supplies static resource URI completions. Framework profiles declare resource source members. External factory metadata supplies identities and signatures, not a fabricated local source file or decompilation result.

## Coherent analysis and protocol caches

`AnalyzeWorkspace` returns the complete loaded project with open XAML overlays plus standalone unknown buffers. `AnalyzeOverlays` preserves the narrower API returning only requested open buffers. Both share the production project compiler and resource graph.

`LspProjectAnalysisCache` deduplicates requests/diagnostics for an immutable compiler + buffer-set pair. Its cancellation domains are deliberate: one caller may stop waiting without cancelling other callers; invalidation retires the shared computation. Revision numbers alone are insufficient identities, so the cache also checks document snapshots. It retains a bounded number of entries and observes retired task failures.

`LspSemanticTokenCache` maintains bounded two-generation histories with server-instance IDs. It copies caller buffers, evicts by recency/document/integer budgets and falls back to full token data for missing or unrelated IDs. Delta replacement indices are aligned to complete five-integer tokens. A token edit is a compact transport change, not a claim of incrementally bound C# semantics.

## Validation

Focused tests cover raw Unicode/entity mapping, nested/ignored namespaces, template shadowing, C# local capture and `nameof`, idempotent formatting with protected content, code actions, resource navigation, shared-analysis cancellation, token eviction/delta reconstruction, and real stdio capability negotiation. New capabilities are only advertised by the server when implemented; edit requests never silently write files.
