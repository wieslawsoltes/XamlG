# Browser Compiler Studio

Compiler Studio hosts production Roslyn/XamlG in WebAssembly with Monaco source editors and actual Avalonia controls. JavaScript supplies editor/browser integration, not an imitation binder or HTML recreation of the view.

## Build and serve

```sh
dotnet workload install wasm-tools
npm --prefix tools/XamlG.Playground install --ignore-scripts --no-audit --no-fund
npm --prefix tools/XamlG.Playground run prepare-assets
dotnet publish tools/XamlG.Playground -c Release -o artifacts/playground
python scripts/serve-playground.py --port 8765 --directory artifacts/playground/wwwroot
```

Open `http://localhost:8765/`. The development server enables public cross-origin asset reads for the isolated preview. Production hosting must likewise allow its runtime assets to be fetched from the opaque-origin frame. Roslyn metadata images are separate from executable runtime assemblies. Publishing preserves Avalonia's stable JavaScript asset URLs.

## Source, semantics and pixels

The studio includes XAML/C# editors, compiler/generated-C# diagnostics, source mappings, syntax and typed-operation inspection, realized visual trees, property/structure editing, undo/redo, project drafts/export and responsive dark/light themes.

Compile and Run capture current buffers instead of relying on delayed notifications. Unrelated renders do not overwrite pending edits. Minimal UTF-16-safe changes permit eligible local subtree reparsing with unchanged-node reuse. Parser-work counters are not an end-to-end complexity claim: text construction, indexing, project linking and binding have separate costs.

Design mode provides real drag/eight-handle resize, snapping, aspect locking, keyboard nudging and cancellation. Property/structure commands are source transactions; see [design and reload](hot-reload.md).

## Code-behind factory identity

The compiler can generate construction factories for eligible `x:Class` roots, including nested classes. Preview and isolated payload construction consume `FactoryMetadataName` when resolving runtime types; C# source generation continues to use `FactoryTypeName`. Construction invokes real user constructors and uses the generated initializer's idempotence guard. Caller-controlled/handwritten initialization and unsupported constructor shapes remain explicit boundaries.

Authoring formatting, rename, code actions and token deltas are exposed through the reusable tooling/LSP libraries. The browser also provides Monaco authoring commands and coordinated project undo for C# and XAML.

## Multi-document resources

The Resources tab manages reusable classless dictionaries/styles in the same project as `View.axaml`. It has a path selector, add/remove controls, source/generated/syntax views and a complete three-document example. Source remains local to the browser. Adding/removing files changes the compilation's resource catalog; unresolved dependencies appear as source diagnostics, not runtime loader failures.

Run emits the view, dictionaries, styles and all C# source files into one assembly. `ResourceInclude`, `StyleInclude` and `MergeResourceInclude` call compiled factories. Changing a source before its debounce timer fires is captured by the next Compile/Run. Export format version 3 includes resource text, additional C# files and current generated files; outdated output is omitted. Draft restoration accepts versions 1, 2 and 3 without executing source.

The **C# files** inspector adds, edits, moves and removes auxiliary `.cs` files.
Models, custom controls and partial code-behind classes share the same Roslyn
compilation as `Code.cs`. Project undo/redo, XAML name refactoring, diagnostics,
MCP document operations and runtime execution include these files. The reusable
`CSharpProjectDocumentStore` bounds source retention and rejects edits from
retired or replaced documents. Generated-file discovery excludes every user C#
document, including files moved between folders.

Resource document count/character limits, normalized relative paths, reserved root paths and revision checks bound the editor store. Replacing a project retires callbacks from previous same-path resource editors. See [resource semantics and export metadata](resources.md).

## Explicit execution modes

**Run preview** executes trusted generated code in the editor tab for visual design, with the studio's browser-origin capabilities. Review code before using it.

**Run isolated** emits the complete assembly as data and executes it inside a separate WebAssembly host in an iframe with `sandbox="allow-scripts"`, without `allow-same-origin`. The opaque-origin frame cannot access editor DOM/storage through same-origin APIs. A dedicated MessageChannel validates source/origin/nonce and bounds requests/responses. Content-security policy limits fetches to required assets.

Frame ownership is established before startup completes. Reset disposes the frame and settles queued/active requests; generation checks discard superseded startup/execution results. The runtime can be restarted without reloading the editor.

Isolation is not an operating-system resource quota. Code may consume CPU/memory or stop responding; browser scheduling does not guarantee independent preemption of every infinite loop. Required public assets remain network-accessible. This is not universally safe arbitrary-code execution.

Both hosts bound loaded preview assemblies because collectible browser load contexts are not assumed. Trusted mode needs page reload to reclaim loaded code; isolated mode can discard its runtime independently. Local visual gestures are disabled in isolated mode, while source edits continue through isolated execution rather than silently loading code into the editor.

## Deployment and acceptance

Pages deploys only from `main`, retains environment protections, validates browser behavior, records the exact source commit in `build.json` and reruns tests against the public URL after verifying that identity.

Acceptance covers real controls/code-behind, inspections, immediate edits, undo/redo, mobile themes, canvas gestures, isolation/reset, resource projects, exports/drafts and dependency errors. These are behavioral tests, not exhaustive pixel or browser-engine certification.

## Semantic authoring commands

Use Rename, Format and Actions in the source toolbar or Monaco command palette/context menu. F2 opens a symbol-rename dialog with an edit preview; applying it is one project undo step. Shift+Alt+F formats XAML source/selection while preserving literal XML content, or normalizes a C# document with Roslyn. Ctrl+. displays applicable source actions. Resource and auxiliary C# editors expose the same commands. An XAML name can be renamed from its XAML declaration/reference or its generated C# field use; both routes update XAML and C# together.

C# editors provide accessible-symbol completion, hover, method/constructor signature help,
definitions and references through the reusable `CSharpLanguageService` in `XamlG.Tooling`.
Definition navigation opens the owning Dockyard document. The generated-C# inspector
selects every emitted file, including loader adapters, as read-only source. The same
operations are exposed as `xamlg_csharp_*` tools, with explicit result bounds and source
revision checks for edits. Interactive editor operations work with agent access disabled.

`CSharpRenameService` verifies compilation and identifier bindings before returning an
atomic source plan. It rejects source collisions, silent local capture, unresolved
compilations, inheritance/interface contracts and generated dependencies requiring
framework-aware edits. The browser routes XAML-generated field renames to XAML's existing
rename service. General C# type/member renames across XAML and inheritance hierarchies
remain outside this service's current coverage. Formatting uses Roslyn's syntax whitespace
normalizer; it does not implement desktop `.editorconfig` formatting options. Source
actions currently offer explicit/inferred local types and verify the resulting type and
diagnostics, including nullable and target-typed expressions.

The project-wide transaction history covers XAML, C#, resource edits and resource additions/removals. Toolbar Undo/Redo and Monaco project shortcuts use that history. New typing is captured before commands, conflicting or stale previews are rejected atomically, and source commands never execute the preview. The main syntax revision remains monotonic across undo so stale visuals cannot be mistaken for the current source.
