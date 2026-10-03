# XamlG

### A Roslyn-native XAML compiler — from source to tools to pixels.

[![Build](https://github.com/wieslawsoltes/XamlG/actions/workflows/ci.yml/badge.svg)](https://github.com/wieslawsoltes/XamlG/actions/workflows/ci.yml)
[![Cross-platform](https://github.com/wieslawsoltes/XamlG/actions/workflows/solution.yml/badge.svg)](https://github.com/wieslawsoltes/XamlG/actions/workflows/solution.yml)
[![Upstream compatibility](https://github.com/wieslawsoltes/XamlG/actions/workflows/upstream-compatibility.yml/badge.svg)](https://github.com/wieslawsoltes/XamlG/actions/workflows/upstream-compatibility.yml)
[![Packages](https://github.com/wieslawsoltes/XamlG/actions/workflows/release.yml/badge.svg)](https://github.com/wieslawsoltes/XamlG/actions/workflows/release.yml)

**[Compiler Studio](https://wieslawsoltes.github.io/XamlG/)** · [Framework compiler](docs/framework-compilation.md) · [Designer and reload](docs/hot-reload.md) · [Language server](docs/language-server.md) · [Validation](docs/validation.md) · [Releases](docs/releasing.md)

XamlG compiles XAML into inspectable, strongly typed C#. The portable compiler consumes Roslyn symbols directly: it does not execute application assemblies, rewrite IL or call XamlX as a production fallback. Build-time generation, designer editing, CLI, workspace, LSP and browser hosts share the same compiler.

> **Development preview.** Implemented framework transforms, visual manipulation, state-preserving reload, project watching and isolated preview have executable coverage. This is not unconditional drop-in compatibility with every Avalonia/XAML application or arbitrary custom framework extension.

## Start from source

.NET SDK **10.0.401** and package versions are pinned.

```sh
git clone https://github.com/wieslawsoltes/XamlG.git
cd XamlG
dotnet build XamlG.slnx -c Release
dotnet test XamlG.slnx -c Release --no-build
```

The default solution contains the libraries, CLI/LSP and local tests without requiring WebAssembly or an upstream checkout. `XamlG.Browser.slnx` and pinned upstream suites are separate opt-in builds.

## One pipeline, multiple hosts

```text
XAML → immutable source syntax → Roslyn binding → typed operations → C#
                ↕                     ↑                 ↓
       revisioned designer edits  framework profiles   runtime graph
                ↑                                       ↓
         editor / CLI / LSP                visual inspection + reload
```

| Component | Responsibility |
| --- | --- |
| `XamlG.Syntax` | Source-preserving parsing/recovery, namespace scopes, spans and incremental subtree reparsing. |
| `XamlG.Roslyn` | Compilation-scoped symbols, namespace metadata, generic/member resolution. |
| `XamlG.Compiler` | Typed construction, conversion, directives, collections, services and delegate/accessor operations. |
| `XamlG.CSharp` | C# lowering, partial classes, direct calls, source mapping and runtime metadata. |
| `XamlG.Frameworks` | Symbol-based framework policies; Avalonia selector, setter and compiled-binding transforms. |
| `XamlG.Runtime` | Scoped services/names, subscriptions, source identities, reversible property updates and structural replacement. |
| `XamlG.Generator` | Incremental generator and transitive MSBuild integration. |
| `XamlG.Tooling` | Analysis/navigation, AST/IR inspection, source-first designer transactions and history. |
| `XamlG.Workspaces` | Project snapshots, trusted MSBuild evaluation, watched/cancellable refresh. |
| `XamlG.LanguageServer` | Bounded JSON-RPC, versioned LSP semantics and cancellation-safe publication. |
| `XamlG.AvaloniaRuntime` | Framework binding lifecycle, realized visuals, gesture surface and interaction-state transfer. |

Core compiler/tooling libraries target .NET Standard 2.0. Workspace/LSP, Avalonia integration and executable hosts target .NET 10; generated-code validation uses that runtime.

## Compile as a library

Supply application source/metadata through Roslyn, including the runtime dependencies required by the selected framework profile. Reuse the evaluated project's parse options when adding generated source.

```csharp
using System;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Frameworks.Avalonia;
using XamlG.Syntax;

public static class XamlCompilation
{
    public static CSharpCompilation AddDocument(
        CSharpCompilation compilation, CSharpParseOptions parseOptions,
        string source, string path, CancellationToken cancellationToken = default)
    {
        var syntax = XamlSyntaxTree.Parse(source, path, cancellationToken);
        var document = new XamlCompiler().Bind(syntax, compilation,
            AvaloniaFrameworkProfile.Create(), cancellationToken: cancellationToken);
        var output = new CSharpEmitter().Emit(document, cancellationToken);
        if (!output.Success)
            throw new InvalidOperationException(string.Join(Environment.NewLine, output.Diagnostics));
        return compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            output.Source, parseOptions, output.HintName, cancellationToken: cancellationToken));
    }
}
```

The library is independent of generator drivers, workspaces, browser APIs and MSBuild execution. Hosts choose framework profiles and compiler passes.

## Avalonia, designer and reload

Selectors compile into public API calls; style setter values resolve the actual registered property type. Compiled bindings use Roslyn-resolved member paths and generated delegates, including object-element bindings, relative sources, indexers, two-way stores and command methods. Reflection binding requires an explicit scoped/build opt-out; it is not a fallback for failed static checking. See [framework compilation](docs/framework-compilation.md).

The designer provides real visual hit testing, drag/eight-handle resize, snapping, keyboard movement, aspect locking, cancellation and source-driven structure edits. A validated gesture commits one revision-checked undo transaction. Reparenting preserves namespace/whitespace meaning or diagnoses an incompatible policy.

Structural reload builds a detached candidate, transfers eligible state by explicit identity/unchanged declarations, publishes once and retires the previous graph's generated subscriptions. Changed source wins over preserved interaction state. It does not promise arbitrary instance-identity preservation or rollback of external side effects in user code.

## CLI and LSP

```sh
dotnet run --project tools/XamlG.Cli -c Release -- \
  compile --file tests/CliSmoke/View.xaml --code tests/CliSmoke/Model.cs \
  --framework Portable --output artifacts/generated --emit-assembly artifacts/generated/View.dll

dotnet tools/XamlG.Lsp/bin/Release/net10.0/XamlG.Lsp.dll \
  --project /absolute/path/App.csproj --trust-project --framework Avalonia
```

The LSP watches compiler inputs by default, coalesces refreshes and rejects stale project results. Its stdio host reserves stdout for protocol frames; `--trust-project` is required because MSBuild/source generators can execute project code. Read [protocol capabilities and publication rules](docs/language-server.md).

## Browser Studio

Monaco XAML/C# editing, generated-code diagnostics, syntax/typed-operation inspection, realized visuals, designer commands, drafts/export, themes and responsive layouts run with the actual compiler/framework. Compile and Run capture current buffers, including immediately typed edits.

**Run preview** executes trusted code in the editor tab for visual design. **Run isolated** emits without loading application code in the editor and executes in an opaque-origin sandboxed frame with a bounded MessageChannel. That boundary blocks editor DOM/storage access, not arbitrary CPU/memory consumption. Read [setup and execution boundaries](docs/playground.md).

## Validation and release

The pinned comparison runs **222 original-XamlX baseline cases** separately from **217 XamlG compatibility cases**. Five internal AST/IL-specific assertions are explicitly outside the source-backend comparison; skipped tests are not counted as success. Additional suites validate framework behavior, source editing, reload, watches, stdio, package consumption and browser interactions. No production package depends on XamlX.

Release CI builds **13 shipping packages**, installs clean tool/package consumers, verifies dependency layout and writes hashes plus exact source provenance. Version tags publish tested GitHub artifacts; NuGet publication is a separate explicit environment-protected action. See [release instructions](docs/releasing.md).

## License

MIT. [Third-party notices](THIRD-PARTY-NOTICES.md) cover optional upstream tests and packaged dependencies.
