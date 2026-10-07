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

## Live runtime inspection

Run a trusted preview and open **Inspectors → Runtime**. Choose visual or logical
relationships, filter by name/type/handle, inspect effective properties and classes,
and edit live values. **Open XAML source** checks the preview's source version before
navigating to the main document or a resource file. Runtime edits affect running
objects; source edits continue through the normal designer and undo history.

The runtime workbench includes object paths and exact method invocation, binding
expressions, style/value frames, resources, routed-event watches and the bounded
change journal. Its **Input** section sends keys, text, mouse and wheel events
through Avalonia's actual input pipeline. Leave Design mode first. Pointer
coordinates are control-local DIPs; Down/Move/Up preserve capture for dragging.
Touch contacts and the full runtime catalog are available through **Tools**.

**Accessibility** reads the actual automation-peer tree, including virtual peers,
and inspects or invokes the provider methods supported by each peer. Advanced
operations show their exact argument schema and current handles/revisions, and
inspection results can be exported. These owner controls work with MCP sharing
disabled; remote clients retain the separate permission gate. Typed runtime input
uses Avalonia's private platform APIs, so `XamlG.AvaloniaRuntime` pins its dependency
to exactly `12.1.3`.

The **Objects** section also inspects returned objects. Property, dictionary and
method results expose an `objectId` and `referenceKind` when a live reference is
available. Use that ID with an empty path to inspect the result, or pass it in a
typed method argument such as `{ "objectId": "…" }`. Tree, accessibility-peer and
retained object IDs are accepted by object-path and dictionary operations; tree
operations such as reparenting still require a current tree node.

Choose a listed public interface and inspect again to read explicit interface
members or invoke their exact signatures. This includes accessibility text-range
objects returned by providers. MCP uses the optional `interfaceName` argument;
member reads/writes apply it to the final path member, and method calls apply it
to the selected target.

The inspector retains at most 512 non-scalar reference objects with absolute
five-minute leases. A lease keeps the original object's identity even if a source
property later changes. It retires when its originating tree node or peer leaves
the preview, when the preview is replaced, or when explicitly released. Inspect
and release these leases under **Objects → Retained objects**, or with
`xamlg_runtime_object_handles` / `xamlg_runtime_object_handles_release`. Releasing
does not call application `Dispose` methods. Expiry releases inspector references
on the dispatcher; every access checks the deadline. A full handle table reports
`referenceError` on the returned value without replaying a completed method.

## Explicit execution modes

**Run preview** executes trusted generated code in the editor tab for visual design, with the studio's browser-origin capabilities. Review code before using it.

**Run isolated** emits the complete assembly as data and executes it inside a separate WebAssembly host in an iframe with `sandbox="allow-scripts"`, without `allow-same-origin`. The opaque-origin frame cannot access editor DOM/storage through same-origin APIs. A dedicated MessageChannel validates source/origin/nonce and bounds requests/responses. Content-security policy limits fetches to required assets.

Frame ownership is established before startup completes. Reset disposes the frame and settles queued/active requests; generation checks discard superseded startup/execution results. The runtime can be restarted without reloading the editor.

Isolation is not an operating-system resource quota. Code may consume CPU/memory or stop responding; browser scheduling does not guarantee independent preemption of every infinite loop. Required public assets remain network-accessible. This is not universally safe arbitrary-code execution.

Both hosts bound loaded preview assemblies because collectible browser load contexts are not assumed. Trusted mode needs page reload to reclaim loaded code; isolated mode can discard its runtime independently. Local visual gestures are disabled in isolated mode, while source edits continue through isolated execution rather than silently loading code into the editor.

## Deployment and acceptance

Dockyard, the runtime/designer inspectors, C# tooling, MCP access and the coding-agent
workbench extend this existing application at
[XamlG Compiler Studio](https://wieslawsoltes.github.io/XamlG/). Pages deploys it only
from `main`, retains environment protections and records the exact source commit in
`build.json`. A branch or draft PR does not replace the public deployment.

To connect the published app, start the companion from a checkout with
`dotnet run --project tools/XamlG.Studio.Host -c Release`. Keep the Pages app open,
choose **Agent access**, enable sharing, and pair `ws://127.0.0.1:4893/bridge` using
the printed **Owner token**. The companion accepts `https://wieslawsoltes.github.io`
by default; no local copy of the web app is needed. Provider keys belong in the
companion environment. External MCP clients use its separate MCP token. The access
dialog includes these setup instructions. If the browser requests local network
permission, allow it for the Pages site to connect to the companion; see
[Chrome's local network access documentation](https://developer.chrome.com/blog/local-network-access).

Both PR acceptance and predeployment tests prepare the `/XamlG/` base path first.
Their browser fixture serves the candidate static assets under the Pages HTTPS
origin; actual companion HTTP/WebSocket traffic and origin checks remain active.
Postdeployment tests verify the exact commit, then run the same suite directly
against the public assets with temporary MCP and provider companions. The fixture
grants browser local network permission for that origin in automation.

To run the Pages candidate locally after a fresh publish:

```sh
python3 scripts/prepare-pages.py --commit "$(git rev-parse HEAD)"
dotnet build tools/XamlG.Studio.Host -c Release
PLAYGROUND_PAGES_PREVIEW=1 python3 scripts/test-browser-studio.py
```

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
