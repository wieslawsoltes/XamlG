# Studio, MCP and coding agents

This is the implementation and acceptance ledger for the Dockyard IDE, reusable MCP
packages and coding-agent harness. The reference is
[VB6 Studio](https://github.com/wieslawsoltes/VB6), including its MCP, coding-agent,
permissions, threads, recovery and workbench documentation. This work adapts those
capabilities to Avalonia XAML and Roslyn C#; it does not introduce a VB6 runtime.

## Required deliverables

- A Dockyard docking workspace hosting the actual editors, project explorer,
  diagnostics, generated code, syntax/semantic inspectors, preview, designer,
  runtime inspectors and agent workbench. Floating, docking, activation, layout
  persistence/reset and editor focus remain usable through UI and automation.
- Reusable typed automation contracts and a discoverable, validated tool catalog.
  UI, in-process agents and external MCP clients use the same live project and
  operations. Source mutations carry revisions and use normal atomic undo/redo.
- Reusable MCP integration using the official C# MCP SDK, with stdio and streamable
  HTTP hosts, resources, prompts, cancellation, notifications and browser session
  routing. Host/browser credentials and cloud provider credentials remain separate.
- Project/document CRUD, bounded source reads/search, atomic range edits,
  formatting, rename, references, definitions, completion and workspace export.
- Real XamlG compilation, Avalonia diagnostics, XAML syntax/bound trees,
  generated sources (including loader adapters), Roslyn C# syntax/semantic trees,
  symbols, diagnostics, compilation settings and emitted artifacts.
- Full live Avalonia visual and logical tree inspection and manipulation: stable
  object identities, source provenance, registered/attached/CLR properties,
  effective values and value priority, property mutation/reset, classes, resources,
  data context, layout, focus, routed events and runtime lifecycle. Operations must
  reject stale/dead object handles and run on the Avalonia dispatcher.
- Source-backed visual designer operations: selection, hit testing, geometry,
  property edits, insertion/deletion/reparenting, arrangement, undo/redo and reload.
- Reusable coding-agent engine with official C# provider SDK adapters, configurable
  models/discovery, streaming public events and lossless native tool continuations.
  Match VB6's OpenAI, Anthropic and Gemini provider choices and account-mode scope.
- Named tasks, drafts and follow-ups; plans and questions; bounded native/public
  histories; independent scope/tool policies; approvals, leases and revocation;
  cancellation; request/tool/token budgets; retries and explicit resume without
  replay; context compaction; queued messages; before/after change review and export.
- Locally verified package consumers, protocol/permission/continuation tests,
  runtime and compiler tests, real browser UI and MCP end-to-end tests, documented
  setup and a new pull request from the isolated worktree.

## Acceptance evidence

Completion is pending. Each deliverable above requires current executable evidence;
a listed tool or a successful build alone is not evidence that its behavior works.
Provider protocol tests must use deterministic transports through the official
SDKs; live paid-account validation, if unavailable, must be reported separately.

The implementation branch is `codex/studio-mcp-agent`, based on `19cb780`.
Main is merged at implementation checkpoints; the latest merged upstream commit
is `46553c8` (including literal/enum conversions, Avalonia class/setter contracts,
precompilation/visibility directives, the remaining compiled-binding paths and
transform contracts, and deferred resource callbacks).
The original checkout contains unrelated local compiler edits and is left intact.
The locally available pinned SDK is `/tmp/xamlg-dotnet-10.0.401/dotnet`.

## Run the current implementation

The IDE is integrated into the existing `tools/XamlG.Playground` application and
its main-only GitHub Pages deployment at `https://wieslawsoltes.github.io/XamlG/`.
Start the companion with `dotnet run --project tools/XamlG.Studio.Host -c Release`
and pair it from **Agent access** on that page after this branch reaches `main`.
The Pages origin is accepted by default; hosting a local web app is optional.
PR/predeployment acceptance uses the same Pages base path and HTTPS browser origin;
public verification also starts real MCP/provider companions. See
[the deployment setup](playground.md#deployment-and-acceptance).

Use the SDK pinned by `global.json`, the `wasm-tools` workload, and Node 22 or later:

```sh
dotnet workload install wasm-tools --skip-manifest-update
cd tools/XamlG.Playground
npm ci --ignore-scripts --no-audit --no-fund
npm run prepare-assets
cd ../..
dotnet publish tools/XamlG.Playground -c Release -o artifacts/playground
dotnet run --project tools/XamlG.Studio.Host -c Release -- --web-root=artifacts/playground/wwwroot
```

The companion binds to `http://127.0.0.1:4893`. Open that address, enable **Agent
access**, and pair `ws://127.0.0.1:4893/bridge` with the local token printed by the
companion as **Owner token**. The host prints separate owner and MCP client tokens.
Set `XAMLG_STUDIO_OWNER_TOKEN` for browser pairing/workbench access and
`XAMLG_STUDIO_TOKEN` for external MCP clients, or let the host generate both.
Configured tokens must be distinct and contain 32–256 non-whitespace characters.
The workbench additionally requires the private lease of the currently paired
browser; detaching cancels its requests and agent run. Sharing begins disabled and disconnecting
revokes in-flight operations. Browser sharing policy and agent run policy both
apply. Runtime actions execute the application running in the preview.

Set `OPENAI_API_KEY`, `ANTHROPIC_API_KEY` or `GEMINI_API_KEY` in the companion
environment to enable the respective official C# SDK adapters. Then use **Coding
agent** to choose a provider, discover models or enter a model ID. Provider
credentials are not sent to browser code, transcript exports or MCP clients.
`OPENAI_ENDPOINT`, `ANTHROPIC_ENDPOINT` and `GEMINI_ENDPOINT` optionally supply
HTTPS gateways or HTTP loopback endpoints for deterministic protocol testing.
The companion rejects redirects and disables SDK retries; the harness owns retry
accounting. The official OpenAI SDK currently marks Responses APIs with
`OPENAI001`; the adapter suppresses that diagnostic.

ChatGPT account mode is available without an API
key: open **Coding agent → ChatGPT accounts**, choose **Continue with ChatGPT**,
complete consent in the opened window, then select **ChatGPT account · ChatGPT
plan usage** for a new task and discover models. A manual loopback launch link
is shown if the window was blocked. Use **Enable ChatGPT plan usage** if identity
sign-in succeeded without plan permission. Account selection and context handoff
remain explicit; existing tasks retain their original account.

The default account store is `XamlG/Studio/ChatGPT` under .NET's local application
data directory. `--chatgpt-store=PATH` selects a separate protected store; keep it
outside the project. One companion owns each store at a time. `--chatgpt=false`
disables account mode, which is useful for independent API-key-only fixtures.
Account registration metadata survives restart. Tokens survive restart only if
**Remember credentials on this computer** was selected; Unix protects these files
with permissions and Windows uses current-user DPAPI encryption. **Sign out**
stops account tasks and attempts remote revocation before clearing local tokens.
The workbench reports when that revocation could not be confirmed.

External MCP clients use authenticated Streamable HTTP at `/mcp`, or launch the
companion with `--stdio=true`. With stdio, the loopback browser bridge remains
available and host logs go to stderr. The defaults accept the official Pages
origin and local ports 4893 (or the configured companion port) and 8765. Set
`--origins=https://your-studio.example` to replace these with exact custom browser
origins. HTTP Host and
Origin checks and the separate local tokens apply independently of cloud credentials.

`XamlG.Automation` has no IDE dependency. Implement `IAutomationHost` or build an
`AutomationCatalog`, add typed handlers, and use `.WithAutomation(host)` on the
MCP SDK builder. Supply the same host and an `IAgentProvider` to `AgentHarness`.
Optional `IAgentWorkspace` support supplies bounded before/after checkpoints and
selective source restoration. `scripts/test-studio-packages.py` is an executable
consumer example that uses only NuGet references outside this repository.

Implement optional `IAutomationCatalogEvents` when a host's tools, resources or
prompts can change. `AutomationCatalog` and `BrowserAutomationBridge` implement it.
The MCP adapter maintains SDK primitive collections, preserving other registered
tools and supporting multiple automation hosts with distinct names. Legacy clients
receive session catalog notifications; protocol `2026-07-28` clients opt in through
`subscriptions/listen`, including stateless HTTP. Each stream receives one initial
acknowledgement and only its requested catalog changes, tagged with the request ID.
There are at most 64 active catalog subscriptions per embedding service provider;
each stream coalesces pending changes with constant-size buffering. Disconnecting or
cancelling releases its observers. Request collections are weakly held and do not
retain completed HTTP requests. An embedding application's explicit subscription
handler takes precedence. Hosts implementing `IAutomationResourceEvents` now expose
resource-content subscriptions through both legacy sessions and modern request streams.
Each client may subscribe to at most 128 exact URIs; pending changes coalesce without
retaining source payloads. The browser bridge forwards source and Avalonia runtime
changes as URI-only notifications. Source/generated documents and runtime properties
have URI templates with percent-encoded path/handle arguments and bounded completion.
Default catalog lists return 100 entries per page; cursors reject a changed catalog.
Explicit embedding list handlers retain ownership of their own pagination.

`WithAutomationTasks` enables the official SDK Tasks extension for event-driven
`xamlg_wait` only. Modern clients opt in per request, poll/update/cancel their own
task, or subscribe to its `taskIds` with `subscriptions/listen`. Ordinary clients
receive synchronous waits. Waits observe source revisions or resource changes;
they do not hold the IDE mutation gate. The companion retains at most 32 tasks for
120 seconds, bound to the authenticated principal and the originating workspace.
The workbench provides owner-only cancellation and clearing of finished tasks.

`xamlg_build_targets/create/read/release` expose immutable project JSON, source ZIP,
managed assembly and portable-PDB snapshots using the existing browser compiler.
Artifacts carry a source revision, SHA-256, byte size and five-minute expiry. Reads
use bounded base64 chunks of at most 256 KiB. At most eight artifacts / 32 MiB total
are retained; revoking or replacing the workspace releases them. Transport-provided
principals own their handles, independently of client names. The Agent access panel
provides local download/release controls, searchable capabilities and metadata-only
activity filtering, pause/follow, export and clearing. It records no tool arguments,
results or credentials.

Queued follow-ups stay local until an explicit `RunQueuedAsync` accepts their ID
and reviewed queue revision. The queue supports editing, reordering and removal,
including during generation, with limits of 16 messages, 100,000 characters per
message and 200,000 total characters. Queue text is excluded from provider context
and transcript exports until accepted. Invalid run arguments and stale queue reviews
leave the message queued; a paused turn must be resumed before dispatching another
message. The workbench preserves its separate composer draft and offers these queue
controls alongside a run review showing the captured task, provider, model, request,
permissions and limits. Every Full Access run requires a fresh acknowledgement.
Request timeout, retry count and tool-result size are independently editable.

Enter or Ctrl+Enter in the message composer opens the same run review as **Run**;
Shift+Enter inserts a line and IME composition never submits. Completed messages
render a bounded Markdown subset with copyable fenced code and HTTP(S) links.
HTML, images and embeds remain text. **Earlier messages** and **Newer messages**
page through retained public history; **Follow latest** returns to the current
response. Scrolling up freezes the reading window. Interrupted public replies
remain visible as incomplete and do not become native provider continuations.

Choose the task-start or latest-run source comparison and select **Refresh
changes** to capture the current project. The comparison includes manual and
other-task edits. Select a document for before/after or unified views; **Open
current document** navigates to its existing editor. Diff lines can target queued
feedback. **Previous change**, **Next change** and **Restore selected change**
operate on consecutive changed lines, with a preview and explicit confirmation.
Whole-file restoration remains available. Source or checkpoint changes disable
feedback/navigation/restoration until refreshed. Task-specific drafts, baseline,
file/block selection, diff display, feedback, queue selection and reading state
survive pane reopening within this page; they are not saved in browser storage.

## Local evidence and remaining work

Full validation is deferred until the remaining feature implementation is complete,
as requested. The evidence below records earlier checkpoints; the latest feature
additions and compiler merges have not yet undergone a full validation run.

Implemented since that checkpoint: tasks bind to the creating browser workspace
lifetime; replacement and revocation disconnect that lifetime. A separate preparation
stage captures source before accepting a reviewed queue entry. Source comparisons
support task-start and latest-run checkpoints. The workbench adds independent task
settings, numeric-only saved defaults, public context handoff, thread copy and reading
position, bounded unified diffs, complete replacement patch export, line-targeted
queued review feedback and explicit source-restore confirmation.

Recovery now retains full provider Retry-After deadlines, requires increased effective
output allowance after output stops, counts unknown failed-attempt usage separately,
reserves bounded results before tool execution and bounds aggregate retained request
context. Optional `AgentHttpHandler` carries canonical HTTP/retry advice through the
official SDK transports. `CompactAsync` creates a paid tool-free public checkpoint,
stages and validates replacement context, retains complete recent native turns, and
preserves original history on failure. Manual compaction is reviewed, and `/compact`
is handled locally. Automatic triggers, model-window estimates, retained turns and
checkpoint output allowance are configurable. The older synchronous `Compact` API
remains a local deterministic checkpoint for embedding compatibility.

Targeted companion and browser compilation succeeded during this implementation;
the full behavioral suites and package consumers are deliberately pending. Earlier
browser fixtures need updating for the new compaction and restore confirmations.
MCP templates, completion, resource notifications and catalog pagination also compile
with the browser and MCP packages; their expanded behavioral coverage is pending.
Tasks, immutable build downloads and the activity/operations controls are implemented
in the same feature-first pass. Targeted companion and browser builds succeeded;
task cancellation/ownership, notification streams and artifact emission/download
still require the final behavioral validation pass.

- The merged native solution passed 1,508 tests after merging main through
  `a331efe` (Avalonia class/setter contracts), including 1,017 real-Avalonia tests
  and the runtime inspector's fifteen tests for live manipulation and source
  provenance for objects and deferred resource keys.
- 44 automation/MCP/agent tests passed, including actual official MCP, OpenAI,
  Anthropic and Gemini
  SDK transports, permission enforcement, native tool continuation, no replay on
  resume, queued messages, compaction and conflict-checked source restoration.
  WebSocket tests cover concurrent admission limits, cancellation, expired owner
  leases and pending calls rejected when their browser disconnects.
  Catalog tests cover legacy and modern stdio, modern HTTP with separate clients
  and subscription filters, multiple automation hosts, separately registered SDK
  tools, cancellation and collection lifetimes. All six automation browser tests
  pass with the updated companion, including catalog publication/removal when the
  real browser pairs and revokes access over a modern HTTP subscription.
- All 21 shipping packages built and passed the release-consumer suite at
  `cf9b8b9`, with consumer fixture updates at `fa15505`. The seven new packages
  built, installed and ran through a separate consumer with an initially empty
  package cache. Temporary caches and the 30 MiB candidate package set were
  removed after validation; the small inventory and logs are retained locally.
- All 45 browser scenarios passed locally after the C# authoring expansion,
  modern MCP subscriptions, reviewed queue dispatch and the setter-contract main
  merge through `a331efe`. The runtime
  browser scenario constructs a C# DataContext, invokes its method,
  installs and updates a real binding, inspects style value frames, and creates,
  reparents and removes controls without changing source. Coverage includes actual rendering, authoring, designer gestures,
  source undo, file moves, editor lifetimes, isolation, mobile layout and HTTP MCP.
- Workbench browser tests use local provider fixtures through all three official
  SDKs and the real companion/browser. Each discovers models, edits XAML, compiles, reviews and selectively
  restores source, compacts context and exports the public thread. No paid
  provider account was used. CI starts and removes its own companion processes.
  All three workbench cases also pass with explicit queued dispatch, editing during
  generation, deliberately delayed reorder responses, cancelled/stale run reviews,
  server rejection of missing Full Access acknowledgement and preserved unsent drafts.
  The native queue tests check atomic bounds, revision conflicts, failed argument
  validation and paused turns that cannot be replaced by a queued prompt.
- All eight automation/workbench browser scenarios also passed after separating
  owner and MCP credentials. HTTP checks reject client access to the workbench,
  owner credentials at the MCP endpoint, missing leases and expired browser leases.
- Multi-file C# source now participates in compilation, syntax/symbol inspection,
  XAML rename, runtime event handlers, project undo, export and draft restoration.
  The reusable source store rejects stale editor writes and validates replacements
  atomically. Its two new tests passed with all 96 Tooling tests. The expanded
  browser suite passed 38 of 40 cases initially; after correcting an Undo readiness
  wait and opening the docked Problems tab, all four cases in the affected C# and
  loader suites passed. Both failures were in test synchronization or navigation.
- All 108 Tooling tests pass with the reusable C# language and rename services.
  C# completion, hover, definitions/references, signatures, formatting, local type
  actions and checked source rename are available through MCP and Monaco. Browser
  acceptance exercises completion with agent sharing disabled, navigation to an
  unopened file, cross-file rename/undo, stale edits, generated-field XAML rename
  and selection of individual generated files. Native cases cover silent binding
  capture, overload identity, constants, nullable/target typing and rejected
  generated/inheritance edits. See `docs/playground.md` for current boundaries.

Runtime tools now include bounded object-path inspection and mutation, loaded type
discovery, public method invocation with Task/ValueTask results, live control tree
changes, event watches, binding expressions and style/value-frame diagnostics.
The browser compiler emits Avalonia's source metadata; `xamlg_runtime_source`
exposes exact object and resource-key locations alongside generated-object source
identities. Keyed location reads do not instantiate deferred resources.
Template-owned visuals require template/source edits; tree operations reject
unsupported ownership. Diagnostic frame metadata uses the loaded Avalonia runtime.

Typed keyboard/text, pointer movement/down/up/click/double-click/wheel, and up to
16 touch contacts now dispatch through Avalonia's actual platform input pipeline.
Pointer coordinates are control-local DIPs, capture persists across drag steps,
and inspector disposal/reset releases its mouse and touch devices. Input rejects
disabled, hidden, detached and stale targets; design mode must be disabled first.
The runtime package deliberately pins Avalonia to exactly `12.1.3`, following
[Avalonia's private API packaging requirement](https://github.com/AvaloniaUI/Avalonia/wiki/Using-private-apis-in-nuget-packages).
Only its input adapter opts into those APIs; updating that dependency requires
checking the adapter and its behavioral coverage against the new version.

Accessibility tools traverse the actual automation-peer tree, including virtual
peers, with stable handles, bounded pages, metadata, relationships and supported
provider interfaces. Provider inspection exposes public values and exact method
signatures; invocation uses typed arguments and revision checks. This covers
invoke/toggle, value/range, selection, scrolling and other providers exposed by the
loaded framework. Peer property/tree observers retire with their handles.

Typed dictionary reads and writes support non-string keys and generic read-only
dictionaries. Object/dictionary inspection acquires at most 128 INPC/collection
observers; background bursts coalesce into explicit wildcard change records on the
Avalonia dispatcher. Watches can be cleared and retire with their tree handles.

The existing Inspectors pane now has a Runtime workbench with visual/logical
navigation, effective properties and classes, input controls, object/method
inspection, bindings/styles/resources/events, accessibility providers, advanced
runtime operations and result export. Owner UI actions use a separate local entry
point with schema/revision checks; MCP and agent transports retain their normal
permission gate. Source navigation validates the preview's source version and can
reveal resource documents as well as the main XAML file. The new runtime APIs and
browser UI compile; their native/browser acceptance tests remain deferred until
feature implementation is complete.

Full reference parity remains in progress: remaining provider protocol/recovery
details and account-mode behavioral acceptance; the remaining Roslyn
authoring surface for the multi-file C# workspace; and the corresponding UI and protocol
acceptance coverage. This ledger does not claim those capabilities from a build
or from the existence of a tool name.

The remaining implementation is concentrated in two areas:

- Remaining Roslyn authoring and symbol-navigation coverage, particularly broader
  semantic source actions and coordinated XAML/C# renames beyond the implemented
  generated-field route. Existing rename rejects unsupported inheritance/generated
  dependencies. Operation/control/data-flow inspection and compiler settings are
  now implemented as described below.
- Remaining reference provider/protocol and recovery edge cases, including exact
  stop/failure classification and continuation behavior across the three SDKs.
  The workbench Markdown, Enter/IME, independent review state, source navigation,
  thread paging and selective block restoration are implemented below; their
  behavioral acceptance remains pending.

After these features, update the acceptance fixtures and run the full native,
browser, MCP, provider-transport, Pages-origin and package-consumer validation.
Close failures, document exact boundaries and update the draft PR. Earlier test
counts in this ledger do not certify the current feature additions. The published Pages
app receives this integration only after the PR reaches main.

Reference-valued method/provider results and explicit property/dictionary reads now
receive bounded retained-object handles, including accessibility text-range and
array results. Object-path reads, writes, method calls, dictionary operations and
typed arguments accept tree, peer and retained IDs. Public interface discovery and
the optional `interfaceName` select explicit interface implementations without
depending on the concrete implementation type being public. Tree-only mutations
continue to require a current tree node. Snapshots and the change journal do not
acquire arbitrary-object leases merely by recording a value.

At most 512 references are retained for five minutes. An origin is a tree node or
peer; children receive independent leases with the same origin. Removed origins,
expired leases and replaced previews reject access. A dispatcher timer releases
expired references; local UI and MCP expose inventory and explicit release without
disposing application objects. Capacity exhaustion returns metadata and an explicit
reference error rather than failing or replaying an already-completed method. The
Runtime workbench includes retained-object navigation, public-interface selection,
member paging and release controls. Targeted runtime-library and browser compilation
passed without warnings or errors. Behavioral coverage remains deferred: verify
GC retention/release, absolute expiry, origin removal, explicit interfaces and typed
peer/text-range arguments during the final native/browser validation pass.

The designer now has path-aware and group selection, independent state revisions,
grid/mode/cancel controls, source hit testing, target bounds/size constraints, and
separate geometry/arrangement plan and apply tools. The reusable geometry planner
supports alignment, matching dimensions, distribution and group transforms. The
Avalonia surface performs group movement/resizing/nudging through an overlay and
publishes one batch of source edits. The workspace planner checks exact source
snapshots, rejects duplicate shared-instance edits and groups changes by document.
Main and resource XAML use one compiler-validated project transaction and undo step.
Owner gestures retain automatic reload; MCP plan/apply and the Designer pane's
reviewed plans use a separate explicit `runtime_run` operation. Reload records the
project revision and rejects compilations superseded by source edits.

The existing Inspectors pane hosts the Designer workbench, including multi-selection,
source navigation, preview-root geometry, arrangement anchors, plan review, apply
and reload. The owner callback includes Designer scope without changing the remote
permission gate. `xamlg://designer` notifies selection, configuration, source and
runtime changes. Targeted library/browser builds pass; behavioral validation remains
pending for group gestures, resource-template provenance, one-step undo, source and
runtime conflicts, min/max constraints, layout-policy behavior, cancellation and
the owner/remote permission boundary. The default policy emits Canvas offsets or
margins and explicit dimensions; it is not a general layout constraint solver.

The Compiler workbench now edits actual Roslyn parse/compilation options: language
version, conditional symbols, nullable/unsafe/overflow, optimization, platform,
output kind, metadata visibility, diagnostics, main type and module name. Metadata
selection uses the assemblies already supplied by the browser host. A validated
`CompilerSettings.json` workspace document participates in revision checks,
atomic source edits, undo, agent review, draft restore and project/source exports.
Unknown or duplicate settings keys and unavailable metadata names are rejected
before publication. Draft/project JSON is version 4; older drafts receive defaults.
The preview requires DLL/AnyCpu output; other targets remain available for compile
and artifact export. Settings changes invalidate cached compilation and preview
admission. Diagnostics include suppressed status and warning-as-error metadata.

`compiler_options_get/set/write` and `compiler_snapshot` expose the same settings,
effective references, source/generated trees and assembly identity. Compiler
controls use the private local owner entry point while remote policies remain in
force. The workbench retains a settings draft across unrelated source edits and
rejects saving over a settings document that changed since the draft was captured.
Auxiliary C# files can be moved through a revision-checked workspace transaction.

The reusable C# service now supplies actual `IOperation` trees, control-flow graphs
and data-flow analysis for source and generated files. MCP exposes
`csharp_operations`, `csharp_control_flow` and `csharp_data_flow` through the same
Compiler workbench catalog. Operation results use flat result-local parent/child
IDs with types, constants, symbols, implicit operations, conversions and operators.
Flow graphs include reachability, branch semantics, regions, locals and captures;
offsets inside local functions/lambdas select their own graphs. Data-flow results
identify the analyzed region and expose declarations, read/write, assignment,
capture and flow sets through a shared symbol table. Counts, depths, metadata and
locations are bounded with explicit truncation; Roslyn's cyclic graphs are never
serialized directly. These operations compile/analyze code without executing it.

Targeted tooling and integrated Playground builds pass without warnings or errors
for compiler settings and flow inspection. Behavioral validation remains pending:
settings persistence/undo/conflicts, diagnostic policies, selected references,
non-preview emission, generated bodies, nested functions, control/data-flow
selection, cancellation and truncation. No full suite was run during this pass.

ChatGPT account mode is now implemented in the reusable OpenAI package and the
existing agent workbench. The companion exposes account operations only through
its owner/session-protected routes. Users can register separate accounts/workspaces,
select an account, request plan consent explicitly, remember credentials, sign
out, and discover account-specific model slugs/display names in provider order.
Each task captures its original registration ID; changing the picker does not
retarget a task or its context handoff. Account requests require an explicit
registration ID, and account mode never falls back to an API key.

The OAuth service follows the official [registration flow](https://developers.openai.com/siwc/token-sharing-open-source/sign-in):
stable host ID, dynamic client registration, exact loopback callback, state/nonce,
PKCE, signed ID-token verification through Microsoft.IdentityModel, issuer/audience,
authorized-party and returning-subject checks, and separate granted-scope checks.
Its temporary listener binds an ephemeral IPv4 loopback port before publishing a
one-use launch ticket. Only the listener redirects to an authorization URL with
the retained ID-token hint; the IDE never receives that URL or the credentials.
Failed initial code exchanges can restart sign-in with the issued registration
ID and fresh state/nonce/PKCE. Explicit plan consent uses `prompt=consent`; ordinary
reauthorization does not force consent.

Registration metadata is persisted even in the default memory-only token mode.
The file store holds an exclusive process lock, rejects links, and writes complete
snapshots atomically. Unix uses a 0700 directory and 0600 files; this is protected
plaintext, not a keychain. Windows encrypts snapshots with current-user DPAPI.
An embedding application can supply `IChatGptCredentialStore` instead. Stored
registrations are bound to their authentication and inference endpoints, preventing a
production credential store from being reused by a local fixture. Deterministic
companion fixtures can set `--chatgpt-auth-origin=http://127.0.0.1:PORT/` and
`--chatgpt-api-endpoint=http://127.0.0.1:PORT/v1/` together with an explicit,
separate `--chatgpt-store=PATH`; both endpoints must use the same loopback origin.
Refreshes are serialized, and replacements are saved before a required new-ID-token check:
a temporarily unavailable signing-key endpoint leaves the replacement pending
validation without replaying an obsolete refresh token. Confirmed terminal refresh
or identity failures clear unusable credentials while retaining the registration.
Sign-out revokes the latest known refresh token, reports unconfirmed remote
revocation, and clears local tokens. `IAgentProviderSession` binds a complete
reusable-harness run/compaction to its account lifetime, including approval waits
and IDE tool execution; sign-out and reauthorization cancel that lifetime.

Account inference uses the official Responses SDK at the [documented public endpoint](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference),
with array input, instructions, streaming, `store:false`, and a function namespace.
The pinned SDK preserves namespace tools and returned namespace fields through its
persistable-model extension. Foreign or missing namespaces are rejected; native
reasoning/continuation items remain intact. Unsupported request fields, including
`max_output_tokens`, are omitted. The UI labels output as a reserve and states that
a response can exceed the remaining local token budget. Quota and eligibility errors
pause without automatic retry or billing fallback; temporary failures retain normal
bounded retry behavior. Account diagnostics retain bounded status/code/parameter,
response shape and request ID, without raw provider bodies or credentials.

Targeted OpenAI-package, companion and Playground builds pass. A small SDK shape
probe verified namespace serialization round trips; it is not account acceptance.
Full deterministic OAuth/provider/browser coverage remains deferred with the rest
of validation: identity and callback rejection, registration retry, stored-session
locking/permissions, token rotation/sign-out races, account-bound tools, consent,
model catalogs, namespace continuations, errors and UI isolation. No actual account
was signed in and no production/paid inference was requested.

Temporary reference clones and superseded publishes are removed when no longer
needed. Package consumer caches are scoped to temporary directories. Large failed
browser traces should be discarded after diagnosis, while small acceptance logs
and representative screenshots can be retained for review.

The reusable harness now supplies `RefreshChangesAsync`, comparison identities,
bounded diff pages and `RestoreBlockAsync`. Blocks are recomputed from the exact
captured before/after strings; clients supply a block ID, never replacement text
or offsets. The resulting full-document transaction retains the exact captured
current source as its guard. It preserves UTF-16 offsets, CRLF/LF and missing-final-
newline state, rejects stale workspace/revision/comparison identities, and uses
the existing compiler-validated project transaction and ordinary Undo. Added or
removed documents and sources over 20,000 lines have no selective block action;
whole-file restoration and complete replacement-patch export remain available.
The diff matrix stays capped at 250,000 cells, with a labelled coarse replacement
for larger middles. UI pages start at 500 rows and grow to 10,000 with further
page navigation. Block previews show at most 20,000 characters per side and label
shortened content explicitly.

Review summaries cache exact file-content identities so unchanged refreshes
preserve targeted feedback. Monotonic comparison versions prevent delayed state
polls from replacing a newer review summary. Per-task presentation retains one
document's bounded diff/preview, selected block, feedback draft/target, baseline,
file selection and scroll position. Source revisions invalidate actions without
recapturing or recomputing diffs on every streamed token. Navigation opens main,
resource and auxiliary C# documents in their normal editor; compiler settings
open the Compiler pane. Current-source navigation verifies the captured revision.

The thread renders a text-only Markdown subset with a budget of 500 formatting steps,
literal fallback and an absolute HTTP(S) link allowlist. Copying messages/code
uses a clipboard fallback that restores focus and selection. Tool events are
collapsible. Bounded public history can be paged in either direction; historical
page responses cap entries at 100 and text at 262,144 characters. Frozen reading
windows, captured partial text, per-task tool expansion and scroll anchors survive
updates and pane reopening. Closing the pane releases DOM listeners and observers.
Failed, cancelled, timed-out or output-limited requests publish bounded incomplete
public text while preserving the existing native continuation and accounting rules.

Targeted companion and integrated Playground builds pass without warnings/errors,
and the JavaScript syntax check passes. Behavioral validation remains deferred:
exact block reconstruction and Undo, stale/project/checkpoint races, unchanged
feedback targets, task/pane isolation, late responses, source navigation, inert
Markdown, keyboard/IME, scrolling/selection, thread paging and incomplete replies.
No full suite or provider inference was run during this implementation pass.
