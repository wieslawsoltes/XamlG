# XamlG

### A Roslyn-native XAML compiler — from source to tools to pixels.

[![Build](https://github.com/wieslawsoltes/XamlG/actions/workflows/ci.yml/badge.svg)](https://github.com/wieslawsoltes/XamlG/actions/workflows/ci.yml)
[![Cross-platform](https://github.com/wieslawsoltes/XamlG/actions/workflows/solution.yml/badge.svg)](https://github.com/wieslawsoltes/XamlG/actions/workflows/solution.yml)
[![Upstream compatibility](https://github.com/wieslawsoltes/XamlG/actions/workflows/upstream-compatibility.yml/badge.svg)](https://github.com/wieslawsoltes/XamlG/actions/workflows/upstream-compatibility.yml)
[![Packages](https://github.com/wieslawsoltes/XamlG/actions/workflows/release.yml/badge.svg)](https://github.com/wieslawsoltes/XamlG/actions/workflows/release.yml)

**[Compiler Studio](https://wieslawsoltes.github.io/XamlG/)** · [Authoring APIs](docs/authoring.md) · [Feature status](docs/feature-status.md) · [Project resources](docs/resources.md) · [Framework compiler](docs/framework-compilation.md) · [Designer/reload](docs/hot-reload.md) · [LSP](docs/language-server.md) · [Validation](docs/validation.md) · [Releases](docs/releasing.md)

XamlG compiles XAML into inspectable, strongly typed C#. The compiler consumes Roslyn symbols directly: it does not execute application assemblies, rewrite IL or call XamlX as a production fallback. Build-time generation, project resource linking, designer editing, CLI, workspace, LSP and browser hosts share the compiler.

> **Development preview.** Implemented transforms, resource linking, visual manipulation, state-preserving reload, watching and isolated preview have executable coverage. This is not unconditional drop-in compatibility with every Avalonia/XAML application or arbitrary custom framework extension.

## Start from source

.NET SDK **10.0.401** and package versions are pinned.

```sh
git clone https://github.com/wieslawsoltes/XamlG.git
cd XamlG
dotnet build XamlG.slnx -c Release -warnaserror
dotnet test XamlG.slnx -c Release --no-build
```

The default solution includes libraries, CLI/LSP and local tests without requiring WebAssembly or an upstream checkout. `XamlG.Browser.slnx` and pinned upstream suites are separate opt-in builds.

## One pipeline, multiple hosts

```text
XAML project → immutable source syntax → Roslyn binding → typed operations → C#
                     ↕                       ↑                ↓
            revisioned editor buffers  framework profiles  factory linking
                     ↑                                        ↓
              designer / CLI / LSP               visual inspection + reload
```

| Component | Responsibility |
| --- | --- |
| `XamlG.Syntax` | Source-preserving parsing/recovery, namespace scopes, spans and incremental subtree reparsing. |
| `XamlG.Roslyn` | Compilation-scoped symbols, namespace metadata, generic/member resolution. |
| `XamlG.Compiler` | Typed construction, conversion, directives, services, delegate/accessor operations and resource contracts. |
| `XamlG.CSharp` | C# lowering, project linking, partial classes, source mapping and compilation-scoped binding/output reuse. |
| `XamlG.Frameworks` | Symbol-based policies; Avalonia selectors, setters, bindings and resource/style includes. |
| `XamlG.Runtime` | Scoped services/names, subscriptions, resource export metadata, source identities and reload primitives. |
| `XamlG.Generator` | Incremental source generator and transitive MSBuild integration. |
| `XamlG.Tooling` | Project/overlay analysis, navigation, AST/IR inspection, bounded documents and source-first designer transactions. |
| `XamlG.Workspaces` | Project snapshots, trusted MSBuild evaluation and watched/cancellable refresh. |
| `XamlG.LanguageServer` | Bounded JSON-RPC, coherent open-document overlays and cancellation-safe publication. |
| `XamlG.AvaloniaRuntime` | Framework bindings/resources, realized visuals, gesture surface and state transfer. |

Core compiler/tooling libraries target .NET Standard 2.0. Workspace/LSP, Avalonia integration and executable hosts target .NET 10; generated-code validation uses that runtime.

## Compile a project as a library

Keep one `XamlProjectCompiler` for a project. Supply its **pre-generation** Roslyn compilation, unchanged syntax instances where possible, physical diagnostic paths, reproducible logical paths, and the selected framework profile. The compiler is not coupled to a generator driver, browser API or MSBuild host.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.CSharp.Resources;
using XamlG.Frameworks.Avalonia;

public sealed class ProjectCompilation
{
    private readonly XamlProjectCompiler _compiler = new();
    private readonly XamlG.Compiler.XamlFrameworkProfile _profile =
        AvaloniaFrameworkProfile.Create();

    public CSharpCompilation AddGeneratedDocuments(
        CSharpCompilation application,
        CSharpParseOptions parseOptions,
        IEnumerable<XamlProjectDocument> documents,
        CancellationToken cancellationToken = default)
    {
        var project = _compiler.Compile(
            documents, application, _profile,
            cancellationToken: cancellationToken);

        if (!project.Success)
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                project.Documents.SelectMany(d => d.Output.Diagnostics)));

        return application.AddSyntaxTrees(project.Documents.Select(document =>
            CSharpSyntaxTree.ParseText(
                document.Output.Source, parseOptions, document.Output.HintName,
                cancellationToken: cancellationToken)));
    }
}
```

Include application/framework/runtime references in the Roslyn compilation and preserve its evaluated parse options. The low-level `XamlCompiler.Bind` and `CSharpEmitter.Emit` APIs remain available for individual documents. Use project compilation when documents contain includes.

## Compiled dictionaries and styles

`ResourceInclude`, `StyleInclude` and `MergeResourceInclude` resolve to generated local factories or public factory exports from referenced XamlG assemblies. Relative/root-relative/assembly-qualified resource URIs are checked at compile time. Cycles and failed dependencies produce diagnostics before emission; unrelated documents can still compile. No generated include calls a runtime XAML parser or binary loader.

Ordinary includes retain separate providers. Merge includes perform eager dictionary merging with later/local value precedence and same-variant theme merging. This is not XamlX's IL-level flattening and does not promise identical allocations. Existing binary-XamlX-only resources are not implicitly translated; referenced resources need XamlG export metadata. Public classless resources and eligible concrete `x:Class` roots export factories. Code-behind factories invoke real constructors, provide initialization services, initialize once, and retire generated sessions on constructor failure. Roots requiring caller-controlled construction remain Populate-only.

Backend-only code-generation failures also suppress dependent factories and recover without poisoning cached caller output.

The project cache reuses raw bindings and generated outputs for unchanged documents when export signatures and the Roslyn compilation remain stable. A value-only edit can bind/emit one document. Catalog/graph validation still performs project-wide work; counters are not end-to-end performance benchmarks. See [resource architecture, API and boundaries](docs/resources.md).

## Avalonia, designer and reload

Selectors lower to public API calls; setter values use the registered property's type. Compiled bindings use Roslyn-resolved paths and delegates, including object-element forms, relative sources, indexers, two-way stores and command methods. Reflection binding is an explicit opt-out, not an error-recovery fallback. See [framework compilation](docs/framework-compilation.md).

The designer has real visual hit testing, drag/eight-handle resize, snapping, keyboard movement, aspect locking, cancellation and source-driven structure edits. Gestures commit revision-checked undo transactions. Reparenting preserves namespace/whitespace meaning or diagnoses an incompatible policy.

Structural reload constructs a detached candidate, transfers eligible state by explicit identities and unchanged declarations, publishes once and retires previous subscriptions. Changed source wins over interaction state. Arbitrary object identities and external side effects in user code are not transactionally preserved.

## CLI and LSP

```sh
dotnet run --project tools/XamlG.Cli -c Release -- \
  compile --file tests/CliSmoke/View.xaml --code tests/CliSmoke/Model.cs \
  --framework Portable --output artifacts/generated --emit-assembly artifacts/generated/View.dll

dotnet tools/XamlG.Lsp/bin/Release/net10.0/XamlG.Lsp.dll \
  --project /absolute/path/App.csproj --trust-project --framework Avalonia
```

Project CLI compilation retains all required generated factories. The LSP watches compiler inputs, combines open XAML buffers into a coherent project overlay and rejects stale project/buffer-set results. Editing an included resource can diagnose its caller without fabricating a new caller version; closing the resource restores its loaded source snapshot. Identical duplicate MSBuild inputs are coalesced, conflicting buffers rejected.

Open C# buffers are included in XAML compilation and name-refactoring snapshots without executing code or writing files; their client versions are preserved in returned edits. Closing a buffer restores loaded source. The server advertises prepare/rename for statically resolved XAML names, document/range formatting, structural and member-spelling code actions, resource links/completions, workspace symbols, and semantic-token full/delta/range support. Root-name rename includes actual C# field uses and `nameof`, while leaving strings, comments, and unrelated locals unchanged. Name references follow template namescopes, not text matching. Analysis is shared once per project/open-buffer snapshot across diagnostics and requests. [Authoring APIs and limits](docs/authoring.md) describe these contracts.

Stdout is protocol-only. `--trust-project` is mandatory because MSBuild/source generators can execute project code. See [capabilities and publication rules](docs/language-server.md).

## Browser Studio

Monaco XAML/C# editing, generated-code diagnostics, syntax/typed-operation inspection, realized visuals, designer commands, themes and responsive layouts use the actual compiler/framework. The **Resources** tab adds, edits, removes and inspects reusable project dictionaries/styles. Compile/Run capture pending source edits, and project drafts/exports include resource documents and all generated output. Restore never executes code.

Monaco authoring commands expose **Rename (F2)** with cross-file preview, **Format (Shift+Alt+F)**, and **Actions (Ctrl+.)**. Project undo/redo restores XAML, resources and C# together. Edits validate the entire captured source snapshot; newer typing invalidates stale refactoring plans. None of these source commands runs the preview.

**Run preview** executes trusted code in the editor tab for visual design. **Run isolated** emits without loading application code in the editor and executes the complete project inside an opaque-origin iframe. It blocks editor DOM/storage access, not arbitrary CPU/memory consumption. See [browser setup and boundaries](docs/playground.md).

## Validation and release

The pinned comparison runs **222 original-XamlX baseline cases** separately from **217 XamlG compatibility cases**. Five internal AST/IL-specific assertions are explicitly outside the source-backend comparison; skipped cases are not successes. No production package depends on XamlX.

Independent compiler, designer/tooling and host jobs create detached Git worktrees with separate build outputs. Additional gates cover Linux/Windows/macOS warning-free builds, native/runtime tests, cross-assembly resources, real stdio watch/overlay tests, installed-package consumers and browser behavior. See [validation](docs/validation.md).

Release CI builds **13 shipping packages**, installs clean tool/package consumers, and writes hashes plus exact source provenance. Version tags publish tested GitHub artifacts; NuGet publication is a separate environment-protected action. See [releases](docs/releasing.md).

## License

MIT. [Third-party notices](THIRD-PARTY-NOTICES.md) cover optional upstream tests and packaged dependencies.
