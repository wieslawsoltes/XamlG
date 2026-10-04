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

## Multi-document resources

The Resources tab manages reusable classless dictionaries/styles in the same project as `View.axaml`. It has a path selector, add/remove controls, source/generated/syntax views and a complete three-document example. Source remains local to the browser. Adding/removing files changes the compilation's resource catalog; unresolved dependencies appear as source diagnostics, not runtime loader failures.

Run emits the view, dictionaries and styles into one assembly. `ResourceInclude`, `StyleInclude` and `MergeResourceInclude` call compiled factories. Changing a resource before its debounce timer fires is captured by the next Compile/Run. Export format version 2 includes resource text and all current generated files; outdated output is omitted. Draft restoration accepts earlier single-document drafts and new project drafts without executing either.

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
