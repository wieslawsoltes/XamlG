# Intelligent Avalonia UI

XamlG provides interactive agent responses using Avalonia XAML, C# expressions, versioned state and explicit tool-data references. `XamlG.IntelligentUI` and `XamlG.IntelligentUI.Avalonia` are reusable .NET 10 libraries without a dependency on Studio. The browser IDE and its MCP companion use these same libraries.

The architectural reference is [OpenUI's article](https://www.openui.com/blog/how-chatgpt-intelligent-ui-works). It is a third-party observation, not an OpenAI protocol specification. Public interoperability targets [MCP Apps](https://modelcontextprotocol.io/extensions/apps/overview); no undocumented ChatGPT wire format is guessed or advertised as compatible.

## Studio workflows

**Coding agent → Intelligent UI → Try intelligent UI** creates a native Avalonia pricing card without a provider key. Its inputs update C# expressions and computed fallback without model inference. **Inspect UI** exposes intelligent XAML, bound JSON, current state, revisions, static XAML export, reactive C# export and snapshot export. Example prices are fictional.

The **Intelligent UI workspace** button opens owner-only controls. Enable **Allow local UI workspace storage**, then use **Save UI workspace**, **Restore saved UI** or **Forget saved UI**. Data is stored in IndexedDB for the current Studio origin and project identity. Save first preserves the normal project draft and its identity so a page reload finds the same archive. Storage is explicit; no archive is silently loaded or overwritten. Another tab's save/delete rejects stale versions. Invalid archives leave the live workspace intact, and restoration invalidates old action reviews. Forget removes the archive, not current in-memory cards.

The same panel contains **Full C# execution**. An agent may submit a proposal, or the owner may enter intelligent XAML and JSON. **Review full C#** freezes the exact source/data and displays its hash. **Approve and run full C#** creates a fresh, disposable opaque-origin frame. **Reset execution frame**, closing the panel, or changing the workspace discards the frame. The frame does not start Studio, receive provider credentials, access the editor's storage, or acquire tool authority. Full C# is executable code: the frame is not a hard CPU/memory quota or an OS process sandbox. Only run trusted code.

Direct provider agents and paired companion agents share the catalog. The provider's native tool result and continuation are preserved; HTML does not replace tool history. UI messages fill the normal Studio composer rather than starting inference. Tool actions require an additional user review and use ordinary schema/revision validation; they do not grant permissions to the coding agent.

## Component catalog

The default catalog has **42 components**, with explicitly typed properties and matching native factories:

| Category | Components |
| --- | --- |
| Layout | StackPanel, Grid, Panel, Canvas, WrapPanel, DockPanel, UniformGrid, Border, ScrollViewer, Viewbox, ContentControl, UserControl |
| Text/actions | TextBlock, SelectableTextBlock, Button, RepeatButton, Separator |
| Inputs/ranges | TextBox, Slider, CheckBox, ToggleButton, RadioButton, ToggleSwitch, ProgressBar, NumericUpDown |
| Selection/hierarchy | ItemsControl, ListBox, ListBoxItem, ComboBox, ComboBoxItem, TabControl, TabItem, TreeView, TreeViewItem, Expander |
| Dates/time | DatePicker, CalendarDatePicker, Calendar, TimePicker |
| Shapes | Rectangle, Ellipse, Line |

Shared properties include sizing, alignment, opacity, visibility/enabling, thickness, accessible names, tooltips, Grid positions/spans, DockPanel placement and Canvas coordinates. Component-specific descriptors cover ranges, selection, item lists, headers, nullable date/time/numeric inputs, and shape geometry. Numeric formats, integer indices, array sizes, selected indices, child types and conflicting content/items are validated before native mutation. A component name is not permission to set arbitrary CLR properties.

Extend `UiCatalog` and `UiAvaloniaCatalog` together with trusted application factories, typed setters and input adapters. The native renderer retains controls by stable key, detaches event handlers on retirement and reconciles content/item containers. It validates transport-provided trees independently and recovers the previous snapshot on setter/conversion failure. Custom application controls require corresponding registration; this is not a claim to cover every third-party Avalonia control automatically.

## Authoring and pure C# expressions

```xml
<StackPanel xmlns="https://github.com/avaloniaui"
            xmlns:ui="urn:xamlg:intelligent-ui" Spacing="12">
  <Slider ui:Key="seats" ui:Bind="seats" Minimum="1" Maximum="50"/>
  <TextBlock Text="{ui:Expr &quot;$&quot; + state.seats * data.unitPrice}"/>
  <Button ui:Key="continue" ui:Action="continue"
          Content="Discuss this configuration"/>
</StackPanel>
```

Publish initial state `{ "seats": 8 }`, data `{ "unitPrice": 29 }`, and action `{ "id": "continue", "kind": "message", "text": "Discuss this configuration" }`. `ui:Key` defines identity; `ui:Bind` connects the component's typed input property. `ui:When` controls conditional realization; `ui:Each` and `ui:ItemKey` provide keyed repetition. Collections for `ItemsSource` are bounded arrays of strings; use keyed child elements for richer content.

The default `csharp-pure` interpreter uses Roslyn syntax trees without compiling or loading model code. It supports literals, JSON members/indices, checked decimal arithmetic, comparisons, short-circuit booleans, null coalescing/conditional access, casts, formatted interpolation, pure math/string functions, and bounded collection queries with lexical lambdas. Filtering, projection, ordering, grouping, aggregation and dictionaries share an evaluation budget. It deliberately rejects arbitrary construction, assignment, reflection, file/network access, and unknown calls even in dead branches. Numeric/text properties remain typed: write `"$" + value`, not a bare number, for a text property.

The full C# backend is separate and opt-in. `UiCSharpExpressionCompiler` compiles genuine Roslyn expressions or statement bodies against host-supplied metadata references. Full C# uses real `JsonElement` APIs, for example:

```csharp
{
    var count = state.GetProperty("n").GetInt32();
    int Square(int value) => value * value;
    return Enumerable.Range(1, count).Select(Square).Sum().ToString();
}
```

The embedding host must approve a `UiCSharpExecutionRequest`, including exact source/generated source, reference identities and hash. Cached compilation still requires approval. Compilation count and result-size limits do not make arbitrary C# a sandbox. The Studio workflow places this backend only in the explicitly approved execution guest. Full-C# archives require the same selected language/compiler and renewed host approval; default pure stores reject them.

`x:Class`, arbitrary markup extensions, CLR namespaces, DTDs and executable XAML event handlers are not silently enabled by either expression backend. Trusted complete projects use the existing XamlG project compiler and its explicit preview execution workflow.

## Revisions and streaming

A new `UiPublish` uses `expectedRevision=0`, `sequence=1`. Read the current snapshot before subsequent publications, use its exact revision and increment its sequence by one. An `isFinal=false` growing XAML prefix may render complete parsed tags while exposing diagnostics for an unfinished suffix. A final malformed document never replaces the last valid view. Partial provider arguments are not executed speculatively.

Document/data revisions and input-state revisions are separate. State mutations and action preparation check both. Delayed data binds check the source revision and workspace lifetime. Each creation has a session identity distinct from its logical ID: release/recreate cannot attach an old transcript card to a new owner's surface. Local archive replacement preserves session identities for transcript reconnection but issues fresh revisions so old reviews cannot become valid again.

## Retained tool data

`UiDataStore` retains successful results under a transport-derived owner, random handle, exact version, content hash and expiry. A reference supports a strict RFC 6901 JSON pointer, array offset/count and object-field projection. Resolution never fetches an arbitrary URL. Defaults allow 64 entries, 16 MiB retained total, 4 MiB per source and a 30-minute lifetime. Resolved pages and final bound UI data have independent limits.

`UiDataAutomation` exposes put/list/read/release/bind tools. With an explicit host predicate it observes successful, authorized read operations through `AutomationCatalog.InvocationCompleted`. It does not invoke nested tools, replace original results or retain denied/executing operations. Studio selects Source, Compiler and Project reads. Retention failures are reported by the reference inventory rather than falsely returning handles. Workspace retirement invalidates retained data, even when no UI cards existed. Handles and their authority are never restored from an archive.

Use `xamlg_ui_data_list` after an authorized IDE read, inspect a bounded page with `xamlg_ui_data_read`, and bind chosen references:

```json
{
  "id": "analysis",
  "expectedRevision": 7,
  "bindings": [
    {
      "name": "diagnostics",
      "reference": {
        "id": "HANDLE_FROM_DATA_LIST",
        "version": 1,
        "pointer": "/diagnostics",
        "offset": 0,
        "count": 25
      }
    }
  ]
}
```

Pass this to `xamlg_ui_data_bind`. Copy the actual handle/version from discovery; do not fabricate them. Every source and the target surface must have the same owner. A failed reference, expired handle, stale revision or oversized merged value leaves the old bound data intact. Explicit small JSON updates remain available through `xamlg_ui_data`.

## Tool inventory

| Tools | Purpose |
| --- | --- |
| `xamlg_ui_catalog`, `xamlg_ui_present`, `xamlg_ui_native_present` | Discover the schema and publish a portable or native MCP response |
| `xamlg_ui_read`, `xamlg_ui_state`, `xamlg_ui_data` | Inspect and update committed UI state/data |
| `xamlg_ui_action`, `xamlg_ui_release`, `xamlg_ui_export` | Prepare inert actions, retire sessions and export source |
| `xamlg_ui_data_put`, `xamlg_ui_data_list`, `xamlg_ui_data_read`, `xamlg_ui_data_release`, `xamlg_ui_data_bind` | Owner-scoped retained results, bounded resolution and atomic binding |
| `xamlg_ui_csharp_propose`, `xamlg_ui_csharp_status` | Non-executing full-C# proposals and their owner-approved results |

All 16 tools are discoverable through the shared agent/MCP catalog. Proposal creation is not execution, and a pending proposal must not be described as completed. Persistence and full-C# approval controls are owner UI actions, not permission-bypassing remote tools.

## Embedding

```csharp
using XamlG.Automation;
using XamlG.IntelligentUI;
using XamlG.IntelligentUI.Avalonia;

var store = new UiSessionStore();
var snapshot = store.Publish(UiExamples.Pricing(), "application-user");
using var view = new UiAvaloniaSession(
    store, UiPresentation.From(snapshot), "application-user");
// Put view.View in your Avalonia host; construct/dispose on the UI thread.
view.ActionRequested += call =>
{
    var intent = store.PrepareAction(call, "application-user");
    // Review intent and then use your existing authorized command path.
};
```

For MCP or an independent coding harness:

```csharp
var catalog = new AutomationCatalog(authorize: YourPermissionReviewAsync);
using var ui = new UiAutomation(catalog, store);
using var data = new UiDataAutomation(catalog, store);
var proposals = new UiCSharpProposals(store);
proposals.Register(catalog);
UiNativeAppResource.Register(catalog, store,
    new Uri("https://your-deployment.example/XamlG/"));
// Feed catalog to AgentHarness, or:
// builder.WithAutomation(catalog).WithAutomationUi();
```

The native asset base must be a trusted HTTPS directory (HTTP loopback is allowed for development). Deploy the published browser assets including `ui-native.html`, `ui-guest-boot.js`, `ui-native-guest.js`, the .NET runtime, Avalonia assets and compiler reference inventory. This guest does not initialize Studio or agent services. The native resource registration can optionally set `useForStandardTools: true`; the default preserves separate portable and native presentation tools.

Supply a transport-derived `AutomationCallContext.PrincipalId`. Display labels and model arguments are not identities. Trusted local methods (`ReadLocal`, `SnapshotLocal`, `ReplaceArchive`, `CreateLocal`, proposal completion/clear) must not be exposed as unrestricted remote APIs.

For desktop persistence, choose a private trusted directory outside source control and use `UiFileArchiveStorage` with `UiWorkspacePersistence`. Reads/writes/deletes use an OS lock and version comparison; writes use a same-directory temporary file, disk flush and atomic replacement. Unix creation permissions are guarded on non-Windows platforms. This is not encryption or protection from another process running as the same user. Archive restoration recompiles declarations and restores no grants, credentials, delegates or provider history. Browser storage follows the same compare-and-swap interface using one IndexedDB transaction across tabs.

## Exports

Static XAML exports resolved properties, nullable values as `x:Null`, and item values as real XAML objects rather than serialized JSON attributes. It is intentionally a static view. Reactive C# exports reconstruct the intelligent source, state, data and action intents using `UiSessionStore`/`UiAvaloniaSession`, and expose `UpdateData` for new application results. The constructor accepts an optional `UiCompiler` and native catalog for trusted extensions. Export never executes code. The normal package consumer compiles and executes generated C# against NuGet references only.

## MCP host rendering

Both resources use `text/html;profile=mcp-app`, `_meta.ui.resourceUri` and extension identifier `io.modelcontextprotocol/ui`. The view/host handshake targets MCP Apps `2026-01-26`; the existing official MCP SDK continues to handle transport versions independently.

**Portable** (`ui://xamlg/intelligent-ui/v1`) is a small trusted DOM projection covering the default catalog with typed inputs, safe text nodes, shapes and content/selection containers. Its CSP denies external network, scripts and frames. CSS layouts and browser-native widgets are functional projections, not pixel-equivalent Avalonia rendering.

**Native** (`ui://xamlg/intelligent-ui/native-v1`) uses an outer MCP Apps bridge and a separately sandboxed URL guest running actual Avalonia/Skia/Wasm. Its resource metadata declares the precise nested frame origin. The outer document needs no `unsafe-eval`; the inner URL document has a deployment-relative runtime CSP. Only the actual parent/guest windows can exchange bounded messages. Native controls use the same renderer as Studio, including theme, state and disposal behavior. A host may restrict nested frames, Wasm or asset loading; the portable resource and computed text remain the interoperability fallback. Native rendering is not a promise of identical pixels across differing fonts, devices, scaling or host policies.

Views call tools through their host and never inherit permission from an annotation. Tool results, state updates, cancellation status, host context and manual refresh are supported. Host-forwarded resource notifications are handled where available; `resources/subscribe` is not assumed to be a portable MCP Apps capability. No resource subscription proposal is treated as an implemented standard.

## Validation and provenance

`tests/XamlG.IntelligentUI.Tests` covers pure/full C#, component/input adapters, transactional state/source/data errors, archives and conflicts, owner isolation, native reconciliation, MCP round trips, proposal non-execution and exported XAML loaded by Avalonia. The test-only runtime XAML loader is not a production dependency.

`tools/XamlG.Playground/ui-app-tests` executes the real embedded portable resource and IndexedDB adapter, including all 42 default types, nullable/date/selection messages, action reviews, hostile text, parent-source checks, stale sessions, cross-tab writes and reloads.

`tools/XamlG.Playground/tests/intelligent-ui.spec.mjs` covers native Studio cards and deterministic OpenAI/Anthropic/Gemini continuations. `intelligent-ui-lifecycle.spec.mjs` covers owner-controlled archive reload/forget, proposed full C# execution in an actual opaque-origin Wasm frame, and the native MCP resource communicating through a real companion and public host protocol. These fixtures use synthetic credentials and do not claim successful paid inference or testing inside every commercial MCP host.

```sh
dotnet test tests/XamlG.IntelligentUI.Tests -c Release -warnaserror
cd tools/XamlG.Playground
npm ci --ignore-scripts --no-audit --no-fund
npx playwright install chromium
npx playwright test --config=playwright.ui.config.mjs
# After a real production publish and Release build of the companion:
python ../../scripts/test-browser-studio.py tests/intelligent-ui.spec.mjs tests/intelligent-ui-lifecycle.spec.mjs
```

The browser workflow publishes the real Wasm/Skia app; `WasmBuildNative=false` is not working-browser evidence. Strict builds use warnings as errors and explicit bash pipefail. CI checks apply to their exact commit, not later source. Release-manifest inclusion, successful package validation, merge status, public Pages deployment and NuGet publication are separate facts; current evidence is recorded in PR #18.
