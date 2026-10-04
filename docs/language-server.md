# Language server

`XamlG.LanguageServer` is the embeddable protocol library; `tools/XamlG.Lsp` supplies the packable `xamlg-lsp` stdio executable. Both use `XamlCompilationSession`, without a generator-driver shim.

```sh
dotnet build tools/XamlG.Lsp -c Release
dotnet tools/XamlG.Lsp/bin/Release/net10.0/XamlG.Lsp.dll \
  --code /absolute/path/Model.cs --reference /absolute/path/Controls.dll --framework Portable
```

Source/reference options are repeatable. Metadata-only mode reads C# and assembly metadata without executing application code. Stdout contains only framed JSON-RPC; host logs go to stderr.

## Trusted projects and automatic refresh

```sh
dotnet tools/XamlG.Lsp/bin/Release/net10.0/XamlG.Lsp.dll \
  --project /absolute/path/App.csproj --trust-project \
  --target-framework net10.0 --framework Avalonia
```

MSBuild evaluation and source generators may execute project-defined code. `--project` requires `--trust-project` before evaluation and cannot be combined with metadata-only inputs.

Watching is enabled by default; `--no-watch` disables it. Evaluated inputs include source/additional/configuration documents, project references, metadata/analyzer files, restore assets and relevant directory-level configuration. Source roots admit newly created files; unrelated outputs are excluded. Embedders can provide an explicit `XamlWatchInputs`. Arbitrary external MSBuild imports not present in this input set are not discovered by a general filesystem scan.

Refresh permits one active loader and a coalesced pending signal. Supersession cancels loading; a noncooperative loader still cannot publish an obsolete revision. Failed evaluation retains the previous compiler snapshot. Atomic saves, renames and watcher overflow request refresh.

## Coherent unsaved resource projects

The server maintains three distinct identities: the Roslyn project revision, each client's document version, and the complete open-buffer-set revision. Successful open/change/close notifications advance the set revision without fabricating versions for other documents. Identical file aliases cannot create two conflicting open buffers for one physical path.

`AnalyzeOverlays` combines all open project XAML buffers with the loaded text of closed documents. Semantic requests and diagnostics use the same dependency graph. Changing a resource can add/remove a diagnostic in an unchanged caller; closing its unsaved buffer restores the loaded on-disk source. The server refreshes affected open-document diagnostics and rejects requests superseded by project or buffer-set changes. Unrelated/untitled buffers remain standalone.

The workspace coalesces identical AdditionalDocuments contributed by multiple MSBuild targets and rejects inconsistent text for a physical path. Logical resource identity remains distinct from the physical path used by LSP.

## Protocol and publication

Implemented: initialize/shutdown, sequential UTF-16 open/change/close synchronization, diagnostics, hover, element/member/value completion, source definitions, open-XAML references/highlights, symbols, folding, full semantic tokens, cancellation and `xamlg/inspect` (syntax, typed operations, source mappings and C#).

Rename, code actions, formatting, semantic-token deltas, C#-side references and decompiled metadata navigation are not advertised. Editors can add host-specific capabilities through reusable tooling.

Publication rechecks project and buffer-set freshness under the output gate. Cancellation can discard queued work but cannot truncate an admitted Content-Length frame. Partial writes, flush failures and deadlines permanently close the transport. See [publication invariants](lsp-publication.md).

## Validation

`test-lsp-host.py` launches the real process and exchanges framed requests. `test-lsp-watch.py` modifies C# input and verifies diagnostics without changing XAML versions. `test-lsp-resources.py` opens a trusted multi-document resource project, supplies an unsaved broken dependency, checks caller diagnostics and inspection, then closes the dependency to verify recovery.

Library tests cover framing, bounds, stale/aliased documents, coherent buffer sets, queued cancellation, partial writes, deadlines and shutdown. Release validation repeats protocol/watch process tests against an installed tool package; the resource-overlay process test runs in the LSP host workflow.
