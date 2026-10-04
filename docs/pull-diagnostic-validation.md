# Pull diagnostics and resource-move validation

The language server exposes `textDocument/diagnostic` and `workspace/diagnostic` over the same immutable XAML/C# project analysis used by its semantic features. Negotiated pull clients can request related documents and refresh notifications; legacy clients retain push diagnostics. The protocol cache is not a semantic cache: current analysis is always obtained before comparing report values.

## Result identity and mutation safety

A previous result ID is an opaque bounded string, including the empty string. Empty/unknown/foreign/evicted IDs return a full report. A valid ID plus equal ordered diagnostic values returns `unchanged`, without an `items` property. Range, severity, code, source and message all participate in equality.

The LRU is bounded by document count and estimated character retention. Oversized reports are returned intact but not retained; an old token for that URI is retired without evicting unrelated reports. Required fields and cost arithmetic are validated before cache mutation. Checked result-ID allocation occurs before any eviction, preserving valid entries even at sequence overflow. IDs are never reused after clear or remove.

These contracts are covered by `DiagnosticCacheTests` and `DiagnosticCacheBoundaryTests`, including Unicode, report ordering/removal, malformed input, capacity pressure and cancellation. `DiagnosticPublicationTests` queues actual diagnostic frames behind a gated stream to test dependency supersession and cancellation of one queued consumer without losing another report. `ProjectAnalysisCacheTests` covers independently cancellable waiters sharing one semantic computation.

## Real-process and installed-tool suites

```sh
dotnet build tools/XamlG.Lsp -c Release
python scripts/test-lsp-diagnostic-contract.py tools/XamlG.Lsp/bin/Release/net10.0/XamlG.Lsp.dll

dotnet restore tests/ResourceWorkspaceSmoke/ResourceWorkspaceSmoke.csproj
dotnet restore tests/FileRenameSmoke/FileRenameSmoke.csproj
python scripts/test-lsp-resources.py tools/XamlG.Lsp/bin/Release/net10.0/XamlG.Lsp.dll
python scripts/test-lsp-pull.py tools/XamlG.Lsp/bin/Release/net10.0/XamlG.Lsp.dll
python scripts/test-lsp-file-moves.py tools/XamlG.Lsp/bin/Release/net10.0/XamlG.Lsp.dll
```

The diagnostic-contract suite runs in both negotiated pull and legacy push modes. It checks empty IDs, rejected inputs followed by valid unchanged reports, workspace versions, unsaved C# dependency failures, recovery without saving, Unicode, removed-document clears, push/pull value equality and clean shutdown.

The resource/pull suites test closed and related documents, broken unsaved includes, unchanged caller versions, cache identity, removal and refresh response ownership. File-move tests exercise file/folder mappings and both workspace-edit encodings without writing application files from the server.

Release validation installs the candidate CLI/LSP into a clean tool directory and repeats every protocol suite against that installed DLL, including resource, pull and file-move coverage. The resource fixture restores supply metadata; they do not replace the tool binary under test. CI records the Git blob identity of each protocol script so evidence is traceable to the tested source.

## Browser gate

Resource move dialogs use a typed `FileMoveDialogState` rather than independent string-valued Razor parameters. Acceptance tests check actual source/destination paths, same-path rejection, live error clearing, previewed source links, atomic apply/undo/redo, stale previews, collisions, cancel/reopen and no implicit application execution. These run against a real WebAssembly publish, not a native type-check substitute.

The PR must pass every final-head workflow before merge. The main-only Pages workflow must subsequently validate the exact public build identity and repeat browser acceptance against the deployed site. A test definition is not evidence that it passed; inspect the workflow logs and artifacts for the specific commit.
