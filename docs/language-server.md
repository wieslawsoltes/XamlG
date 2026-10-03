# Language server

`XamlG.LanguageServer` is an embeddable protocol library. `tools/XamlG.Lsp` is the executable stdio host and can be packed as the `xamlg-lsp` .NET tool. It uses the same `XamlCompilationSession` as the compiler CLI and designer, without a generator-driver shim.

## Metadata-only mode

```sh
dotnet build tools/XamlG.Lsp -c Release
dotnet tools/XamlG.Lsp/bin/Release/net10.0/XamlG.Lsp.dll \
  --code /absolute/path/Model.cs \
  --reference /absolute/path/Controls.dll \
  --framework Portable
```

The process reads UTF-8, Content-Length-framed JSON-RPC on stdin and writes only framed responses to stdout. Diagnostics from the host go to stderr. Source and reference options are repeatable. C# inputs are parsed and assembly inputs are inspected as metadata; they are not executed in this mode.

## Trusted project mode

```sh
dotnet tools/XamlG.Lsp/bin/Release/net10.0/XamlG.Lsp.dll \
  --project /absolute/path/App.csproj \
  --trust-project \
  --target-framework net10.0 \
  --framework Avalonia
```

Project evaluation and application source generators may execute project-defined code. The host rejects `--project` without `--trust-project`, before opening the project. Project mode and metadata-only input mode are deliberately mutually exclusive.

## Implemented protocol surface

Initialization and shutdown; open/change/close synchronization with sequential UTF-16 ranges; versioned compiler diagnostics; hover; element/member/value completion; source definitions; references among open XAML documents; document highlights and symbols; folding; full semantic tokens; cancellation; and the `xamlg/inspect` extension for syntax, typed operations, source mappings and generated C#.

The protocol advertises only implemented capabilities. Rename, code actions, formatting, semantic-token deltas and C#-side references are not advertised. Go-to-definition currently returns source locations, not decompiled metadata. Compilation reflects the project snapshot loaded at startup. An embedding host can call `UpdateCompilation`; the stdio executable does not yet watch C# files, project files or NuGet restore changes automatically.

## Validation

`python scripts/test-lsp-host.py tools/XamlG.Lsp/bin/Release/net10.0/XamlG.Lsp.dll` launches the real executable and exchanges framed messages. It checks the trust boundary, stdout integrity, semantic requests, source diagnostics, malformed-source recovery, sequential Unicode edits, closing and shutdown. Separate library tests exercise malformed/oversized headers, truncated payloads, document budgets and stale revisions.
