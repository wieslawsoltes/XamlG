# Browser Compiler Studio

The studio hosts `XamlG.Tooling`, framework profiles and the production Roslyn/C# backend in Blazor WebAssembly. JavaScript supplies Monaco and browser integration; it does not reimplement the XAML binder. The preview renders actual Avalonia controls.

## Build and serve

From the repository root:

```sh
dotnet workload install wasm-tools
npm --prefix tools/XamlG.Playground install --ignore-scripts --no-audit --no-fund
npm --prefix tools/XamlG.Playground run prepare-assets
dotnet publish tools/XamlG.Playground -c Release -o artifacts/playground
python3 -m http.server 8765 --directory artifacts/playground/wwwroot
```

Open `http://localhost:8765/`. Publishing copies compiler reference metadata separately from executable WebAssembly assets and preserves the stable Avalonia JavaScript module URLs required by static hosting.

## Editing and inspection

XAML and C# have separate editor models. Compile and Run read both current buffers explicitly, so an immediate command cannot compile the previous debounced text. Unrelated UI renders do not overwrite in-flight editor text. Programmatic document changes cancel pending change notifications. Designer edits and undo/redo compile their managed snapshot rather than recapturing a superseded editor model.

Full-buffer notifications are converted to a minimal UTF-16 replacement by `XamlTextDiffer`. Eligible local edits reparse one complete element; unaffected, unmoved nodes retain their identity. Recovery-sensitive edits use the full parser. The existing syntax snapshot is passed directly into browser compilation. The Pipeline panel reports reparsed character and reused node counts, not an end-to-end performance claim: full text construction and line indexing still process document-sized data.

Syntax and bound-tree nodes can reveal source spans. Selecting a stale analysis after a newer source edit is rejected. Visual inspection reports realized framework visuals, their names, bounds and visibility, including children produced by control templates. Source-based property editing is not yet a general drag/resize visual designer.

## Execution boundary

Nothing is sent to a compiler service: source is analyzed inside the browser. The browser still downloads application assets and reference metadata from the static host.

Run executes the resulting assembly in the same browser tab and origin as the studio. It is **not** a security sandbox. Run only trusted code. Arbitrary user code can consume memory, block the UI or invoke browser APIs available to the application. Restoring a draft does not run it. The host caps loaded preview assemblies at 64 because collectible browser load contexts are not assumed; export and reload to reclaim them.

The current preview uses complete recompilation/replacement on Run. Runtime graph/reload primitives exist, but structural state-preserving hot reload, isolated preview execution and project-scale browser workspaces are not claimed.

## Deployment and tests

`pages.yml` deploys only `main`, preserving the repository's deployment environment protections. It publishes the site, runs acceptance tests, sets the `/XamlG/` base URI and writes `build.json` containing the exact source commit. After Pages deployment, CI checks that public identity and reruns the same browser tests against the public URL.

Browser acceptance checks cover compiling and running controls, code-behind, visual/syntax inspectors, mobile layout and theme switching, immediate-edit Run, document switching and undo/redo. They are behavioral tests, not comprehensive pixel-parity or browser-engine certification.
