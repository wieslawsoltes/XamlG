# Compiler Studio editor lifetimes

Monaco editor lifetime is separate from both the Blazor component and the shared JavaScript module. Resource moves, undo/redo and source selection replace keyed components while module imports, editor creation and buffer reads may still be asynchronous.

## Ownership

`EditorInteropModule` owns one application-scoped import and a unique facade object. Individual `EditorInteropSession` instances borrow that facade, own one editor identity and own their managed callback reference. Disposing an editor cannot dispose the module or invalidate sibling editors. The application retires all sessions before disposing its facade/import.

`CodeEditor.DisposeAsync` marks the component retired and detaches its session before the first cleanup await. Session retirement is also synchronous and idempotent. A late create result is cleaned exactly once, rather than published back to the retired component. An admitted read/write completes before JS editor deletion and callback release; a read that completes after retirement returns no buffer.

The managed source-command path reads a command payload under the editor's operation gate and invokes the managed handler only after releasing that gate. Awaiting a JS-to-.NET authoring callback while holding the gate would deadlock when that callback captures the invoking editor again.

## Source integrity

Resource capture checks the exact component, source path, editor generation, immutable source snapshot and component retirement state before and after interop. Selection captures its outgoing live buffer before requesting a keyed replacement. A retired capture is discarded; it is never replaced with stale parameter text. Failures in a live editor remain errors instead of being silently treated as a cache hit.

Same-parameter renders do not overwrite pending Monaco input. Parameters changed during async creation are applied to the live session after creation. Project move/history operations retain their atomic source/identity transactions. The existing move, one-step undo/redo and repeated selection/export tests remain unchanged.

## Tests

Native tests link the actual component and interop sources into the tooling test project. Fake JS references enforce the disposal contract and explicit task barriers exercise retirement during initialization and active reads, sibling survival, failure cleanup, parameter changes, callback retirement and recursive command capture. Parameter setup uses Blazor's `ParameterView` rather than suppressing component analyzer warnings.

Production browser tests exercise actual Monaco controls, before-debounce selection capture, full export, model retirement across repeated replacements and root-buffer survival. These complement rather than replace the existing resource move/undo/redo acceptance tests. Passing native lifecycle tests alone does not certify the browser renderer or WebAssembly host.
