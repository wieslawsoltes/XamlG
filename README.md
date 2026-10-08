# XamlG

### A Roslyn-native XAML compiler — from source to tools to pixels.

[![Build and MSBuild](https://github.com/wieslawsoltes/XamlG/actions/workflows/ci.yml/badge.svg)](https://github.com/wieslawsoltes/XamlG/actions/workflows/ci.yml)
[![Upstream compatibility](https://github.com/wieslawsoltes/XamlG/actions/workflows/upstream-compatibility.yml/badge.svg)](https://github.com/wieslawsoltes/XamlG/actions/workflows/upstream-compatibility.yml)
[![Theme construction](https://github.com/wieslawsoltes/XamlG/actions/workflows/theme-corpus.yml/badge.svg)](https://github.com/wieslawsoltes/XamlG/actions/workflows/theme-corpus.yml)
[![Packages](https://github.com/wieslawsoltes/XamlG/actions/workflows/release.yml/badge.svg)](https://github.com/wieslawsoltes/XamlG/actions/workflows/release.yml)

**[Compiler Studio](https://wieslawsoltes.github.io/XamlG/)** · [Authoring APIs](docs/authoring.md) · [Feature status](docs/feature-status.md) · [Project resources](docs/resources.md) · [Framework compiler](docs/framework-compilation.md) · [Designer/reload](docs/hot-reload.md) · [LSP](docs/language-server.md) · [Validation](docs/validation.md) · [Releases](docs/releasing.md)

XamlG compiles XAML into inspectable, strongly typed C#. The compiler consumes Roslyn symbols directly: it does not execute application assemblies, rewrite IL or call XamlX as a production fallback. Build-time generation, project resource linking, designer editing, CLI, workspace, LSP and browser hosts share the compiler.

> **Development preview.** Implemented transforms, resource linking, visual manipulation, state-preserving reload, watching and isolated preview have executable coverage. Compatibility is defined by the supported public contracts and exact-revision validation, not unconditional compatibility with every XAML application or arbitrary custom framework extension. A merged change is not a published NuGet version.

## Start from source

.NET SDK **10.0.401** and package versions are pinned.

```sh
git clone https://github.com/wieslawsoltes/XamlG.git
cd XamlG
dotnet build XamlG.slnx -c Release -warnaserror
dotnet test XamlG.slnx -c Release --no-build
```

The default solution includes libraries, CLI/LSP and local tests without requiring WebAssembly or an upstream checkout. `XamlG.Browser.slnx` and pinned upstream suites are separate opt-in builds.

## Replace the Avalonia compiler through MSBuild

Use the single-reference `XamlG.Avalonia` integration package in an existing C# Avalonia project. Set `XamlGVersion` to a built/validated candidate version and configure its NuGet feed; [the release guide](docs/releasing.md) explains candidate creation and clean consumer validation.

```xml
<ItemGroup>
  <PackageReference Include="XamlG.Avalonia" Version="$(XamlGVersion)" />
</ItemGroup>
```

This package supplies the generator, transitive build targets and matching runtime dependencies. While `XamlGEnabled` is true, its targets disable Avalonia's XamlX compilation and name generator, configure the pinned Roslyn interceptor contract and enable unsafe compilation for generated deferred-resource callbacks. Eligible handwritten `AvaloniaXamlLoader.Load` calls, generated initializers and factories share initialization ownership. Unsupported loader paths receive diagnostics instead of silently invoking a runtime XAML compiler.

The input normalizer reconciles `XamlGSource`, Avalonia XAML/resource items and existing `AdditionalFiles`. Project removals and updates remain authoritative; physical paths are separated from logical resource identities, conflicting metadata is rejected, and unrelated generator metadata is preserved. Input preparation precedes analyzer-config generation and compilation. Unchanged input manifests retain their timestamps; edit, rename, clean/rebuild, linked-file and multi-target behavior have executable contracts.

A project-body opt-out leaves Avalonia's normal compiler settings intact:

```xml
<PropertyGroup>
  <XamlGEnabled>false</XamlGEnabled>
</PropertyGroup>
```

Do not globally force the Avalonia compiler or name generator back on while XamlG is enabled: conflicting global properties are diagnosed. Source-generation integration uses the pinned SDK/Roslyn contract; older compiler hosts are not implied to be compatible.

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
| `XamlG.Compiler` | Typed construction, conversion, directives, services, lazy options, delegate/accessor operations and resource contracts. |
| `XamlG.CSharp` | C# lowering, project linking, partial classes, loader adapters, source mapping and compilation-scoped reuse. |
| `XamlG.Frameworks` | Symbol-based policies; Avalonia selectors, setters, bindings and resource/style includes. |
| `XamlG.Runtime` | Scoped services/names, subscriptions, resource exports, initialization ownership and reload primitives. |
| `XamlG.Generator` | Incremental source generator and transitive MSBuild integration. |
| `XamlG.Tooling` | Project/overlay analysis, navigation, AST/IR inspection, bounded documents and source-first designer transactions. |
| `XamlG.Workspaces` | Project snapshots, trusted MSBuild evaluation, resource-emission inputs and watched/cancellable refresh. |
| `XamlG.LanguageServer` | Bounded JSON-RPC, coherent open-document overlays and cancellation-safe publication. |
| `XamlG.AvaloniaRuntime` | Framework bindings/resources, realized visuals, gesture surface and state transfer. |
| `XamlG.Avalonia` | One package reference for the generator, build integration and Avalonia runtime dependencies. |
| `XamlG.Automation` | Typed, schema-validated tools, project scopes, permission profiles and revocable run leases. |
| `XamlG.Mcp` | Official C# MCP SDK integration, resources/prompts and authenticated browser bridge. |
| `XamlG.Agents` | Provider-independent tasks, streaming events, approvals, budgets, compaction and source change review. |
| `XamlG.Agents.OpenAI` | Official OpenAI Responses SDK adapter with lossless native tool continuation. |
| `XamlG.Agents.Anthropic` | Official Anthropic Messages SDK adapter retaining signed thinking and native tool results. |
| `XamlG.Agents.Gemini` | Official Google Gen AI SDK adapter retaining thought signatures and native function results. |
| `XamlG.Studio.Host` | Installable `xamlg-studio` companion for MCP clients and the Dockyard agent workbench. |

Core compiler/tooling libraries target .NET Standard 2.0. Workspace/LSP, Avalonia integration and executable hosts target .NET 10; generated-code validation uses that runtime.

## Compile a project as a library

Keep one `XamlProjectCompiler` for a project. Supply its **pre-generation** Roslyn compilation, unchanged syntax instances where possible, physical diagnostic paths, reproducible logical paths and framework profile. The compiler is not coupled to a generator driver, browser API or MSBuild host.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.CSharp.Integration;
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
        {
            var diagnostics = project.Documents
                .SelectMany(document => document.Output.Diagnostics)
                .Select(diagnostic => diagnostic.ToString())
                .Concat(project.SourceIntegration.Diagnostics.Select(
                    diagnostic => diagnostic.ToString()));
            throw new InvalidOperationException(
                string.Join(Environment.NewLine, diagnostics));
        }

        return XamlCSharpCompilation.AddGeneratedSources(
            application, project, parseOptions, cancellationToken);
    }
}
```

The shared composition API includes project-level loader adapters as well as document factories and preserves compilation-wide parse-feature consistency and tree-specific analyzer configuration. **Do not add only `project.Documents[*].Output.Source`**: that discards the loader adapter. Use `XamlCSharpCompilation.Sources(project)` when exporting the complete generated source set.

Include application/framework/runtime references and preserve evaluated parse options. The low-level `XamlCompiler.Bind` and `CSharpEmitter.Emit` APIs remain available for individual documents. Use project compilation when documents contain includes. Assembly-emission hosts must also supply evaluated managed resource inputs; generated C# alone does not embed assets.

## Compiled dictionaries, styles and bindings

`ResourceInclude`, `StyleInclude` and `MergeResourceInclude` resolve to generated local factories or public factory exports from referenced XamlG assemblies. Relative/root-relative/assembly-qualified resource URIs are checked at compile time. Cycles and failed dependencies produce diagnostics before emission. No generated include calls a runtime XAML parser or binary loader.

Ordinary imports and eager merges preserve dependency order, local precedence and same-variant theme merging. Theme keys are evaluated once and initialize the provider before its contents. This is not XamlX's IL-level flattening and does not promise identical allocations. Existing binary-XamlX-only resources are not implicitly translated: referenced resources need XamlG export metadata.

Public classless resources and eligible concrete `x:Class` roots export factories. Code-behind factories invoke real constructors, forward initialization services, initialize once and retire generated sessions on constructor failure. Roots requiring caller-controlled construction remain Populate-only. Backend failures suppress dependent factories and recover without poisoning cached caller output.

Selectors lower to public API calls while tracking the selected child separately from its template owner. Setters use registered property types. Compiled bindings use Roslyn-resolved paths and delegates; failed compiled paths are never retried through reflection. Element-form reflection bindings, including nested `MultiBinding` values, capture their current namescope and services. Named control references follow framework metadata and template-local namescopes.

Option extensions lower to lazy, typed branches without constructing unselected values. Transform-operation literals retain primitive operations and interpolation behavior rather than being reduced to matrix-only transforms. See [framework compilation](docs/framework-compilation.md).

The project cache reuses raw bindings and generated outputs when export signatures and the Roslyn compilation remain stable. A value-only edit can bind/emit one document. Catalog/graph validation still performs project-wide work; counters are not end-to-end performance benchmarks. See [resource architecture and boundaries](docs/resources.md).

## Designer and reload

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

Project CLI compilation retains factories, loader adapters and evaluated resource-emission inputs. The LSP watches compiler inputs, combines open C# and XAML buffers into one coherent snapshot, and rejects stale results. Editing a resource can diagnose its caller without inventing a new caller version; closing a buffer restores its loaded source. Identical duplicate inputs are coalesced and conflicting buffers rejected.

Prepare/rename, formatting, structural/member-spelling code actions, file/folder refactoring, resource links/completions, workspace symbols, pull diagnostics and semantic-token full/delta/range support share project analysis. Name refactoring includes actual C# field uses and `nameof` while preserving strings, comments and unrelated locals. It returns versioned edits without implicitly writing files. [Authoring APIs and limits](docs/authoring.md) describe the contracts.

Stdout is protocol-only. `--trust-project` is mandatory for the LSP because MSBuild/source generators can execute project code. See [capabilities and publication rules](docs/language-server.md).

## Browser Studio

Monaco XAML/C# editing, generated-code diagnostics, syntax/typed-operation inspection, realized visuals, designer commands, themes and responsive layouts use the actual compiler/framework. The **Resources** tab adds, edits, removes and inspects reusable dictionaries/styles. Compile/Run capture pending edits; project drafts/exports include resource documents and generated output. Restore never executes code.

**Rename (F2)**, **Format (Shift+Alt+F)** and **Actions (Ctrl+.)** expose source authoring. Project undo/redo restores XAML, resources and C# together. New typing invalidates stale refactoring plans. Source commands do not execute previews.

**Run preview** executes trusted code in the editor tab for visual design. **Run isolated** emits without loading application code in the editor and executes in an opaque-origin iframe. It blocks editor DOM/storage access, not arbitrary CPU/memory consumption. See [browser setup and boundaries](docs/playground.md).

## Validation and release

Seven PR workflows own native/MSBuild, real-host, upstream, theme, browser and package validation; Pages deployment is separate. Independent jobs use detached worktrees and verify source integrity. Workflow definitions and historical results do not validate a newer revision.

The pinned comparison executes **222 original-XamlX baseline cases** separately from **217 XamlG runtime/diagnostic cases**. Five internal AST/IL-specific assertions are outside that source-backend comparison. Additional shared regressions run through both compilers and are counted separately from the original upstream cases. The separate theme gate compiles the original Simple and Fluent documents/code-behind and realizes seventeen controls in Light and Dark variants; compile-only success is insufficient. No production package depends on XamlX.

Release CI builds **21 shipping packages**, installs clean consumers and records hashes/source provenance. Tagged publication additionally requires upstream, theme-construction and browser validation. NuGet publication is an explicit environment-protected action. See [validation](docs/validation.md), [releases](docs/releasing.md) and the [Studio implementation ledger](docs/studio-agent-implementation.md).

## License

MIT. [Third-party notices](THIRD-PARTY-NOTICES.md) cover optional upstream tests and packaged dependencies.
