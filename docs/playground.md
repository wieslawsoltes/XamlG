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

Compiler metadata downloads retry transient HTTP or transport failures up to three
attempts, with bounded backoff and `Retry-After` support. Permanent errors and server
delays above 15 seconds stop automatic retries. If loading fails, **Retry loading**
starts it again without reloading the page or discarding source edits.

The initial browser runtime also limits parallel downloads and retries transient
failures through its boot-resource hook, including in isolated previews. Studio's JavaScript
modules share a loader that retries failed downloads up to three times without
creating separate editor or agent registries. A failure before the application
starts shows **Reload the page**; compiler metadata failures after startup use
**Retry loading** and preserve the active editor buffers. Runtime integrity checks
and the isolated preview's credential and content-security restrictions still apply.

## Source, semantics and pixels

The studio includes XAML/C# editors, compiler/generated-C# diagnostics, source mappings, syntax and typed-operation inspection, realized visual trees, property/structure editing, undo/redo, project drafts/export and responsive dark/light themes.

Each source file opens as a Dockyard document tab. Documents can split, dock, float,
close and reopen from Explorer or **View**; closing captures pending edits and keeps
the project file. Generated files open as read-only documents. Inspectors, Coding
agent and Agent access are separate tool panels, available from **Tools**. Use
**View → Reset layout** to restore the default arrangement. Project, editing and
execution commands live in the compact top menus; Compile, Run and Undo/Redo remain
in the toolbar.

Compile and Run capture current buffers instead of relying on delayed notifications. Unrelated renders do not overwrite pending edits. Minimal UTF-16-safe changes permit eligible local subtree reparsing with unchanged-node reuse. Parser-work counters are not an end-to-end complexity claim: text construction, indexing, project linking and binding have separate costs.

**Auto compile** and **Auto preview** are on by default. Owner source edits compile
after a 600 ms typing pause, then refresh the current preview mode when compilation
succeeds. Invalid edits keep the last valid view. Use **Run → Automatic updates**
to pause compilation or keep compilation on while pausing preview execution; the
preview header also has an **Auto on/off** toggle. Preferences survive reloads.
Manual Run remains available. Automatic updates include XAML, C#, project resources,
compiler settings, source authoring commands and undo/redo. They preserve isolated
mode, and pending updates retire when the workspace changes. Agent/MCP source edits
do not schedule automatic execution; their runtime operations retain their own
permission checks.

Design mode provides group selection, real drag/eight-handle resize, snapping, aspect locking, keyboard nudging and cancellation. **Tools → Designer** adds source geometry previews, alignment, equal sizing and distribution. Property/structure commands and multi-document geometry edits use workspace transactions; see [design and reload](hot-reload.md).

## Code-behind factory identity

The compiler can generate construction factories for eligible `x:Class` roots, including nested classes. Preview and isolated payload construction consume `FactoryMetadataName` when resolving runtime types; C# source generation continues to use `FactoryTypeName`. Construction invokes real user constructors and uses the generated initializer's idempotence guard. Caller-controlled/handwritten initialization and unsupported constructor shapes remain explicit boundaries.

Authoring formatting, rename, code actions and token deltas are exposed through the reusable tooling/LSP libraries. The browser also provides Monaco authoring commands and coordinated project undo for C# and XAML.

## Multi-document resources

The **Resources** tool manages reusable classless dictionaries/styles in the same project as `View.axaml`. It has a path selector, add/remove controls, links to source/generated documents and syntax inspection, and a complete three-document example. Source remains local to the browser. Adding/removing files changes the compilation's resource catalog; unresolved dependencies appear as source diagnostics, not runtime loader failures.

Run emits the view, dictionaries, styles and all C# source files into one assembly. `ResourceInclude`, `StyleInclude` and `MergeResourceInclude` call compiled factories. Changing a source before its debounce timer fires is captured by the next Compile/Run. Export format version 4 includes resource text, additional C# files, compiler settings and current generated files; outdated output is omitted. Draft restoration accepts versions 1–4 and follows the automatic preview preference. Disable Auto preview before restoring a draft that should only be inspected.

The **C# files** inspector adds, edits, moves and removes auxiliary `.cs` files.
Models, custom controls and partial code-behind classes share the same Roslyn
compilation as `Code.cs`. Project undo/redo, XAML name refactoring, diagnostics,
MCP document operations and runtime execution include these files. The reusable
`CSharpProjectDocumentStore` bounds source retention and rejects edits from
retired or replaced documents. Generated-file discovery excludes every user C#
document, including files moved between folders.

Resource document count/character limits, normalized relative paths, reserved root paths and revision checks bound the editor store. Replacing a project retires callbacks from previous same-path resource editors. See [resource semantics and export metadata](resources.md).

## Live runtime inspection

Run a trusted preview and open **Tools → Runtime properties**. Choose visual or logical
relationships, filter by name/type/handle, inspect effective properties and classes,
and edit live values. **Open XAML source** checks the preview's source version before
navigating to the main document or a resource file. Runtime edits affect running
objects; source edits continue through the normal designer and undo history.

The runtime tool panels include object paths and exact method invocation, binding
expressions, style/value frames, resources, routed-event watches and the bounded
change journal. Each panel docks independently and shares the selected object.
The **Input** panel sends keys, text, mouse and wheel events
through Avalonia's actual input pipeline. Leave Design mode first. Pointer
coordinates are control-local DIPs; Down/Move/Up preserve capture for dragging.
Touch contacts and the full runtime catalog are available through **Runtime tools**.

**Accessibility** reads the actual automation-peer tree, including virtual peers,
and inspects or invokes the provider methods supported by each peer. Advanced
operations show their exact argument schema and current handles/revisions, and
inspection results can be exported. These owner controls work with MCP sharing
disabled; remote clients retain the separate permission gate. Typed runtime input
uses Avalonia's private platform APIs, so `XamlG.AvaloniaRuntime` pins its dependency
to exactly `12.1.3`.

The **Runtime objects** panel also inspects returned objects. Property, dictionary and
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
and release these leases under **Runtime objects → Retained objects**, or with
`xamlg_runtime_object_handles` / `xamlg_runtime_object_handles_release`. Releasing
does not call application `Dispose` methods. Expiry releases inspector references
on the dispatcher; every access checks the deadline. A full handle table reports
`referenceError` on the returned value without replaying a completed method.

## Explicit execution modes

**Run preview** executes trusted generated code in the editor tab for visual design, with the studio's browser-origin capabilities. Auto preview uses this mode initially and follows the mode selected by the owner. Pause automatic preview when inspecting code that should not run.

**Run isolated** emits the complete assembly as data and executes it inside a separate WebAssembly host in an iframe with `sandbox="allow-scripts"`, without `allow-same-origin`. The opaque-origin frame cannot access editor DOM/storage through same-origin APIs. A dedicated MessageChannel validates source/origin/nonce and bounds requests/responses. Content-security policy limits fetches to required assets.

Frame ownership is established before startup completes. Reset disposes the frame and settles queued/active requests; generation checks discard superseded startup/execution results. The runtime can be restarted without reloading the editor.

Isolation is not an operating-system resource quota. Code may consume CPU/memory or stop responding; browser scheduling does not guarantee independent preemption of every infinite loop. Required public assets remain network-accessible. This is not universally safe arbitrary-code execution.

Both hosts bound loaded preview assemblies because collectible browser load contexts are not assumed. Trusted mode needs page reload to reclaim loaded code; isolated mode can discard its runtime independently. Local visual gestures are disabled in isolated mode, while source edits continue through isolated execution rather than silently loading code into the editor.

## Coding agents

Open **Coding agent → Connection** and choose one of three transports:

- **Direct API** runs in the browser with an OpenAI, Anthropic or Gemini API key.
  Accept browser key exposure, discover or enter a model, and create a task in
  **Tasks**. No companion, MCP connection or project sharing is required. Keys stay
  in page memory and clear on provider changes, pane closure or **Clear credentials**.
- **Local provider relay** uses the browser agent with a loopback companion as its
  provider transport. Set provider keys in the companion environment; enter its
  origin and Owner token in Connection. Provider keys remain on the server, and
  this mode also works without MCP pairing.
- **Paired companion** runs the agent in the companion and supports ChatGPT account
  sign-in. Pair the workspace through Agent access first. Its sharing policy and
  the agent's run policy both apply.

The workbench has Conversation, Tasks, Plan, Changes, Queue, Permissions, Tools and
Activity sections. Permissions control the next run and expose active grants for
revocation. Source approvals show Before/After excerpts and a full-review download;
Changes supports review, selective restoration and normal project Undo. Tasks keep
separate drafts, queues, context and review state. The tool catalog is available
inside the coding-agent panel. See the [agent guide](studio-agent-implementation.md#run-the-current-implementation)
for account setup, limits, compaction and recovery.

Fresh sessions open directly to Connection. Enter provider credentials, choose a
model, and use **Continue to tasks** to start. Section badges show pending requests,
tasks, plan steps, changes and queued messages. The task picker and compact status
stay above the active panel; the message composer stays below the scrolling thread.
Task cards, plan progress, permission groups, searchable tool metadata and the
activity timeline adapt to docked and floating widths in both themes.

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
by default; no local copy of the web app is needed. Relay and companion provider
keys belong in the companion environment. External MCP clients use its separate MCP
token. The access tool panel includes these setup instructions. If the browser requests local network
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
definitions, type definitions, implementations, references and a hierarchical document
outline through the reusable `CSharpLanguageService` in `XamlG.Tooling`. Navigation opens
the owning Dockyard document. The **C# output** tool opens every emitted file,
including loader adapters, as a read-only document. The same
operations are exposed as `xamlg_csharp_*` tools, with explicit result bounds and source
revision checks for edits. Interactive editor operations work with agent access disabled.
MCP additionally exposes declaration search, namespace/type members and type hierarchies.
Hierarchy bases can come from metadata; derived types and implementations are found in
the compilation's source and generated declarations. Inspection reports truncation and
distinguishes editable locations from generated locations.

`CSharpRenameService` verifies compilation and identifier bindings before returning an
atomic source plan. It rejects source collisions, silent local capture, unresolved
compilations and contracts owned by referenced assemblies. Source interfaces, overrides,
record positional members, namespaces and aliases participate in source rename.
`XamlProjectRenameService` adds resolved XAML references, including code-behind class
names, namespace URIs, type names, property elements, compiled binding members, event
handlers, static/enum values and XAML names. It regenerates the project and checks C#
identifiers, XAML symbols, construction and member access before returning source edits.
The existing editor dialog and MCP share this planner; `xamlg_xaml_rename_preview` and
`xamlg_csharp_rename_preview` expose the same exact edit plans. Applying a plan uses one
revision-checked project undo transaction.

Avalonia registration fields, wrappers and attached accessors follow their naming
contract together. Literal registration names are updated; `nameof` follows the renamed
symbol. XAML selections use the logical property/event name. Referenced contracts and
shared/custom registration factories require changes at their owning declaration;
ordinary strings and reflection bindings are not inferred as symbol references. Generated
files remain read-only outputs. Unsupported or ambiguous references that fail regenerated
compilation/binding checks reject the entire plan without publishing edits.

Formatting uses Roslyn's syntax whitespace normalizer; it does not implement desktop
`.editorconfig` formatting options. Source
actions offer explicit/inferred local types, explicit/target-typed object creation,
qualified type/member names, predefined type keywords, constant-preserving `nameof`
expressions, and method/property expression or block bodies. Candidates must compile;
the broader rewrites also check types, constants, selected symbols and surviving
identifier bindings. Actions that would discard comments/directives are omitted.
These are specific compiler-checked rewrites, not a general Roslyn workspace code-fix
catalog. Applying an action uses the existing revision-checked project undo transaction.

The project-wide transaction history covers XAML, C#, resource edits and resource additions/removals. Toolbar Undo/Redo and Monaco project shortcuts use that history. New typing is captured before commands, conflicting or stale previews are rejected atomically, and source commands never execute the preview. The main syntax revision remains monotonic across undo so stale visuals cannot be mistaken for the current source.
