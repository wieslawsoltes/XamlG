# Language server

`XamlG.LanguageServer` is the embeddable protocol library; `tools/XamlG.Lsp` supplies the packable `xamlg-lsp` stdio executable. Both use `XamlCompilationSession`, without a generator-driver shim.

```sh
dotnet build tools/XamlG.Lsp -c Release
dotnet tools/XamlG.Lsp/bin/Release/net10.0/XamlG.Lsp.dll \
  --code /absolute/path/Model.cs --reference /absolute/path/Controls.dll --framework Portable
```

Source/reference options are repeatable. Metadata-only mode parses C# and reads assembly metadata without executing application code. The process reserves stdout for framed JSON-RPC; host logs go to stderr.

## Trusted projects and automatic refresh

```sh
dotnet tools/XamlG.Lsp/bin/Release/net10.0/XamlG.Lsp.dll \
  --project /absolute/path/App.csproj --trust-project \
  --target-framework net10.0 --framework Avalonia
```

MSBuild evaluation and application source generators may execute project-defined code. `--project` is rejected without `--trust-project` before evaluation. It cannot be combined with metadata-only source/reference inputs.

Automatic watching is enabled by default; `--no-watch` disables it. The evaluated project supplies source/additional/configuration documents, project references, metadata/analyzer files, restore assets and relevant directory-level build configuration. Source roots allow newly created files to trigger reevaluation; unrelated build outputs are excluded. `XamlWatchInputs` can supply an explicit set for embedding hosts. Unlisted arbitrary external MSBuild imports are not discovered by a general filesystem scan.

The refresh pump allows one active loader and a coalesced pending signal. Supersession cancels active loading; a loader that ignores cancellation still cannot publish an old revision. Failed evaluation retains the last published compiler snapshot. Atomic saves/renames and watcher overflow request refresh. The LSP cancels affected requests, recomputes diagnostics for open documents and tracks project revision separately from the client's XAML revision.

## Protocol

Implemented: initialize/shutdown, open/change/close synchronization with sequential UTF-16 ranges, versioned diagnostics, hover, element/member/value completion, source definitions, open-XAML references/highlights, symbols, folding, full semantic tokens, cancellation and `xamlg/inspect` for syntax/bound operations/source maps/C#.

Rename, code actions, formatting, semantic-token deltas, C#-side reference search and decompiled metadata navigation are not advertised. Editors can add host-specific capabilities through the reusable tooling layer.

Publication rechecks project/document freshness under the output gate. Request cancellation cannot truncate an admitted Content-Length frame. Partial writes, flush errors and frame deadlines permanently close the transport rather than allowing another message to corrupt it. See [publication invariants and tests](lsp-publication.md).

## Validation

`test-lsp-host.py` launches the actual executable and exchanges framed messages. `test-lsp-watch.py` changes its C# input and verifies updated diagnostics without changing the XAML version. Library tests exercise framing, bounds, stale revisions, queued-message cancellation, partial writes, deadlines and shutdown. Release validation repeats process tests against the installed tool, not just the repository build.
