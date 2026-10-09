# Intelligent Avalonia UI

The intelligent UI pipeline gives agents interactive, locally computed responses rather
than only Markdown. It is implemented as reusable .NET libraries, an optional MCP Apps
resource and native Avalonia cards in Compiler Studio. The architecture is inspired by
[OpenUI's article](https://www.openui.com/blog/how-chatgpt-intelligent-ui-works).
That article is a third-party description, **not a public ChatGPT DIL specification**.
This implementation does not claim private ChatGPT wire-format compatibility.

## Use the Studio integration

Open **Coding agent → Intelligent UI → Try intelligent UI**. This example requires no
provider key. A native Avalonia slider changes the price locally; the annual toggle
reveals a conditional calculation. **Inspect UI** exposes the same revision-checked
input state, intelligent XAML, bound data and computed text fallback. Export either the
resolved static Avalonia XAML, a reactive C# component, or the complete inert snapshot.
The example prices are fictional demonstration data.

An agent discovers `xamlg_ui_catalog` and `xamlg_ui_present` in its starter tool set.
The normal agent run policy reviews publication as an Agent/Edit operation. The tool
result carries a small presentation marker and useful computed text; the conversation
renders its owner-session's native card. Direct OpenAI, Anthropic and Gemini agents
and paired companion agents use the same automation catalog. Provider response
continuations are not replaced by rendered HTML or generated C#.

Actions require a separate user review. A message action fills Studio's composer; it
**does not send a request or spend model tokens**. Tool actions execute only after
explicit local user review and still use the actual catalog's schemas, source revisions
and workspace serialization. The review does not grant permissions to an agent. Remote
MCP Apps actions are mediated by their host's tool/message/link permissions.

## Reuse the libraries

`XamlG.IntelligentUI` targets .NET 10 and depends on the shared automation contracts and
Roslyn C# syntax APIs. `XamlG.IntelligentUI.Avalonia` adds typed native rendering. Neither
library references Compiler Studio. They are included in the release manifest; a merged
source commit is not evidence that a package version has been published to NuGet.

```csharp
using XamlG.Automation;
using XamlG.IntelligentUI;
using XamlG.IntelligentUI.Avalonia;

// Construct native views and dispose them on the Avalonia UI thread.
var store = new UiSessionStore();
var snapshot = store.Publish(UiExamples.Pricing(), "application-user");
using var view = new UiAvaloniaSession(
    store, UiPresentation.From(snapshot), "application-user");

// Put view.View in a native ContentControl, Window or other host.
view.ActionRequested += call =>
{
    var intent = store.PrepareAction(call, "application-user");
    // Show a user review, then use the application's existing authorized command path.
    // Preparing an intent neither performs an action nor grants its authority.
};
```

To expose the same store through an automation host:

```csharp
var catalog = new AutomationCatalog(authorize: async (review, cancellation) =>
    await YourPermissionReviewAsync(review, cancellation));
using var ui = new UiAutomation(catalog, store);
// Use catalog as IAutomationHost in AgentHarness or in the MCP SDK builder:
// builder.WithAutomation(catalog).WithAutomationUi();
```

Supply a transport-derived `AutomationCallContext.PrincipalId`. Display labels and
model-provided arguments are not identities. The agent harness supplies a separate
`agent:<task-id>` identity, and Studio preserves it for intelligent UI calls.
`ReadLocal`, `ChangeStateLocal`, `PrepareActionLocal` and `CreateLocal` are explicitly
trusted embedding-UI paths; never expose them as unreviewed remote endpoints.

## XAML and expression vocabulary

```xml
<StackPanel xmlns="https://github.com/avaloniaui"
            xmlns:ui="urn:xamlg:intelligent-ui" Spacing="12">
  <Slider ui:Key="seats" ui:Bind="seats" Minimum="1" Maximum="50"/>
  <TextBlock Text="{ui:Expr &quot;$&quot; + state.seats * data.unitPrice}"/>
  <Button ui:Key="continue" ui:Action="continue" Content="Discuss this configuration"/>
</StackPanel>
```

Publish with initial state `{ "seats": 8 }`, data `{ "unitPrice": 29 }`, and an action
such as `{ "id": "continue", "kind": "message", "text": "Discuss this configuration" }`.
`ui:Key` identifies a stable control; `ui:Bind` connects the component's declared input
property. `ui:When` controls conditional realization. `ui:Each` and `ui:ItemKey` repeat
an element over a bounded JSON array, with stable item identities. `state`, `data` and
`item` are JSON values, not arbitrary CLR objects.

The default catalog contains StackPanel, Grid, Border, TextBlock, Button, TextBox,
Slider, CheckBox, ProgressBar, Separator and ScrollViewer, with explicitly registered
properties. Extend `UiCatalog` and `UiAvaloniaCatalog` together with application-authored
factories/setters. Adding a native component does not automatically add a corresponding
MCP HTML projection; update that projection deliberately.

`{ui:Expr ...}` parses a bounded **C# expression subset**, not general C#. It supports
JSON members/indices, literals, checked decimal arithmetic, comparisons, short-circuit
booleans, null coalescing, conditional expressions and string interpolation. Numeric
and text properties stay typed; write `"$" + value` for text. It intentionally excludes
calls, construction, assignments, reflection, lambdas and user methods. It does not
compile or load model code. `x:Class`, arbitrary events, bindings, markup extensions,
CLR namespaces, DTDs and external entities are not accepted by this surface compiler.
The normal XamlG application compiler remains available for trusted project code.

## Revisions, streaming and tool data

A new `UiPublish` uses `expectedRevision=0` and `sequence=1`. Read the committed snapshot
before subsequent source updates, use its exact revision and increment its sequence by
one. Send `isFinal=false` for a growing XAML prefix: complete parsed tags can render while
an unfinished suffix has diagnostics. A final malformed document never replaces the
last successful view. This is explicit tool-call streaming, not speculative execution
of arbitrary partial model tool arguments.

Source/data revisions and interaction-state revisions are separate. State edits check
both. Delayed tool data checks the source revision. Stable state names/types retain the
user's input when new source is published. Stable native keys retain controls across
value changes and compatible tree updates. Each created surface also has an opaque
session identity; release/recreate cannot attach an old transcript card to another
owner's new surface. Mutation revisions never reset during a store's lifetime.

`xamlg_ui_data` supplies bounded JSON from a tool result without reconstructing the view.
The current contract passes JSON explicitly; it does not implement a provider-private
reference-expression language or an unbounded result history. Sessions are in memory,
released explicitly or on workspace replacement. Text markers remain readable after a
live session is retired. Task transcript persistence is not persistent live UI state.

## MCP Apps and text-only clients

The implementation uses the public [MCP Apps extension](https://modelcontextprotocol.io/extensions/apps/overview):
`_meta.ui.resourceUri`, the `io.modelcontextprotocol/ui` capability, and resource
`ui://xamlg/intelligent-ui/v1` with MIME type `text/html;profile=mcp-app`.
The resource negotiates MCP Apps protocol `2026-01-26`, receives structured tool results,
reads current owned snapshots, sends host-mediated tools/messages/links and publishes
state context without implicitly starting inference. Resource-content subscriptions
are optional host capabilities; a manual Refresh remains available.

Compiler Studio renders actual Avalonia controls. The external MCP Apps resource is a
small DOM projection of the same resolved operations, because generic MCP hosts embed
HTML rather than arbitrary .NET controls. It is not claimed to be pixel-identical to
Avalonia. Clients without MCP Apps get meaningful computed text and structured results.
A permissive tool annotation is never treated as authorization.

## Safety and resource bounds

The default limits are 64 KiB of XAML characters, 512 expanded nodes, 24 XAML levels,
2,048 expression characters/128 AST nodes, 128 state keys, 32 actions, 32 live surfaces
and 128 KiB of JSON data per object. Text and numeric slots have explicit bounds.
The renderer only invokes trusted native factories and setters. The MCP resource has
no external scripts, network connections, frames, images or model-authored JavaScript;
its transport pins the parent window/origin and its DOM uses text nodes rather than
HTML interpolation. URL actions allow HTTP(S) without embedded credentials.

These are validation and quota boundaries, not a general sandbox for hostile native
extensions. Application-authored factories, existing executed project previews and
host tool implementations retain their own trust requirements. In particular, the
text fallback and tool data may contain untrusted text; never treat it as an instruction
to widen permissions.

## Validation

`tests/XamlG.IntelligentUI.Tests` covers expression rejection, parsed prefixes, stable
repeated keys, numeric and state validation, transactional failures, owner isolation,
source/state conflicts, native interaction/reparenting, session retirement, inert
actions, metadata, resources and export syntax. The MCP resource has standalone
Playwright protocol tests in `tools/XamlG.Playground/ui-app-tests`; real Studio/provider
integration tests are in `tools/XamlG.Playground/tests/intelligent-ui.spec.mjs`.

```sh
dotnet test tests/XamlG.IntelligentUI.Tests -c Release -warnaserror
cd tools/XamlG.Playground
npm ci --ignore-scripts --no-audit --no-fund
npx playwright install chromium
npx playwright test --config=playwright.ui.config.mjs
```

The existing browser workflow publishes the real Wasm/Skia application and runs the
full Studio suite. `WasmBuildNative=false` does not validate a working Skia browser
build. The intelligent UI workflow uses bash pipefail so build/test errors cannot be
hidden by log capture. Exact-commit execution evidence is recorded in PR #18; tests
present in source are not by themselves evidence that a newer revision passed.
