# Browser Compiler Studio

The studio hosts the production Roslyn/XamlG compiler in WebAssembly with Monaco source editors and actual Avalonia controls. JavaScript provides editor/browser integration, not an imitation binder or HTML recreation of the view.

## Build and serve

```sh
dotnet workload install wasm-tools
npm --prefix tools/XamlG.Playground install --ignore-scripts --no-audit --no-fund
npm --prefix tools/XamlG.Playground run prepare-assets
dotnet publish tools/XamlG.Playground -c Release -o artifacts/playground
python scripts/serve-playground.py --port 8765 --directory artifacts/playground/wwwroot
```

Open `http://localhost:8765/`. The supplied development server enables public cross-origin asset reads needed by the opaque-origin preview. Production hosting must likewise allow its static runtime assets to be fetched from the isolated frame. Metadata images used by Roslyn are separate from runtime assemblies. Publication preserves Avalonia's required stable JavaScript asset URLs.

## Source, semantics and pixels

The studio includes XAML/C# editors, compiler and generated-C# diagnostics, source mappings, source-preserving syntax, typed bound operations (including delegate bodies), realized visual-tree inspection, property/structure editing, undo/redo, drafts/export and dark/light responsive layouts.

Compile and Run capture the current editor buffers rather than trusting delayed notifications. Unrelated renders do not overwrite pending edits. Buffer changes are converted to minimal UTF-16-safe replacements; eligible local edits reparse one complete element and reuse unaffected nodes. The existing snapshot reaches semantic analysis unchanged. Parser-work counters are not an end-to-end complexity claim: text construction, line indexing and binding have their own costs.

The Design control enables real drag/eight-handle resize, snapping, aspect locking, keyboard nudging and cancellation. Property and structure commands operate on source transactions. See [design and reload](hot-reload.md) for transaction rules and state preservation.

## Explicit execution modes

**Run preview** executes trusted generated code in the editor tab and enables local visual design. It has the same browser-origin capabilities as the studio. Review code before using this mode. Restoring a draft does not execute it.

**Run isolated** emits the assembly as data in the editor and loads it in a separate WebAssembly host inside an iframe with `sandbox="allow-scripts"`, without `allow-same-origin`. The frame has an opaque origin and cannot read the editor DOM, cookies or local storage through same-origin APIs. A dedicated MessageChannel uses source/origin checks and a nonce handshake; payloads and responses are bounded, and requests have deadlines. A restrictive content-security policy limits resource fetches to required assets. Reset discards the frame and its runtime.

Isolated execution is not an operating-system process/resource quota. Untrusted code can still consume CPU/memory or stop responding, and browser scheduling does not guarantee that every infinite loop is independently preemptible. The policy permits the public static assets necessary to start .NET/Avalonia, not an absolute no-network environment. Do not describe this as universally safe arbitrary-code execution.

Both runtime hosts cap loaded preview assemblies because collectible browser load contexts are not assumed. Local trusted preview requires exporting/reloading the page to reclaim loaded code; the isolated host can be discarded separately. Local visual gestures are disabled while isolated mode is active; source-based edits continue through the isolated execution path rather than silently loading code into the editor.

## Deployment and acceptance

Pages deployment runs only from `main`, retains its environment protections, verifies browser behavior, writes `build.json` with the exact source commit and tests the public deployment after confirming that identity. Acceptance tests include real controls/code-behind, source/visual inspection, immediate-edit Run, undo/redo, responsive themes, canvas drag/resize/cancellation and opaque-origin DOM/storage separation. They are behavioral tests, not exhaustive pixel/browser-engine certification.
