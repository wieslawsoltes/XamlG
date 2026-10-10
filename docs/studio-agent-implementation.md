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

The expanded parity implementation requires the acceptance described here. Candidate `6fd67a3` passed the historical checks below,
but those checks did not cover VB6's direct browser API-key agent mode, independent
agent permissions and complete workbench controls. The user also requires separate
Dockyard inspector/access tool panes and source-file document tabs. The corrected
[feature parity audit](studio-agent-parity.md) tracks these requirements.
[PR #9](https://github.com/wieslawsoltes/XamlG/pull/9) records the current acceptance,
merge and deployment evidence; merge requires the expanded checks to pass.
The expanded native solution passes 2,071 tests, and all 21 shipping packages pass
inventory and clean consumer checks. Direct browser SDK and local relay tests pass
for all three providers; 17 document, runtime and editor regressions also pass.
Full browser and current-head CI evidence is recorded in the PR.
Provider tests use deterministic transports through the official SDKs; production
account sign-in and paid inference have not been exercised.

The implementation branch is `codex/studio-mcp-agent`, based on `19cb780`.
Main is merged at implementation checkpoints; the latest merged upstream commit
is `7a48cd7` (including literal/enum conversions, Avalonia class/setter contracts,
precompilation/visibility directives, the remaining compiled-binding paths and
transform contracts, deferred resource callbacks, implicit child collection contracts
and adder precedence, compilation metadata caching, eager parent-stack services,
implicit provider/metadata/runtime-context contracts and explicit component roots).
The original checkout contains unrelated local compiler edits and is left intact.
The locally available pinned SDK is `/tmp/xamlg-dotnet-10.0.401/dotnet`.

## Run the current implementation

The IDE is integrated into the existing `tools/XamlG.Playground` application and
its main-only GitHub Pages deployment at `https://wieslawsoltes.github.io/XamlG/`.
Open **Coding agent → Connection** to choose a transport:

- **Direct API** runs the shared harness in the Pages/browser app. Enter a provider
  key, accept browser exposure, discover or enter a model, and create a task.
  OpenAI, Anthropic and Gemini use their official SDK models and services. Keys
  are remembered per provider by default in private browser storage. Closing the
  pane or changing provider cancels requests and drops live clients; **Forget
  connection** removes saved credentials and their previous stored version.
- **Local provider relay** uses the same browser harness but sends SDK requests
  through the companion's fixed provider routes. Set API keys in the companion,
  then enter its loopback origin and Owner token in Connection. No MCP pairing or
  project sharing is required. The token can spend the configured API quota.
- **Paired companion** retains server-run agents and ChatGPT account mode. Its
  agent run policy and the browser sharing policy both apply.

Conversation, Connection, Tasks, Plan, Changes, Queue, Permissions, Tools and
Activity organize the workbench. Permissions show the current host ceiling and
active lease/grants separately from preferences for the next run. Source approvals
include revision-bound Before/After excerpts with a full-review download.

Each source or generated file opens in a Dockyard document tab. Closing a source
tab captures its pending edits and keeps the project file. Inspector and Agent
access windows are Dockyard tools available from **Tools**; each runtime inspection
section can dock, float or close independently. **View → Reset layout** restores
the standard arrangement. Layout storage contains identities and geometry, not
source text, credentials or agent transcripts.

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
Paired companion mode additionally requires the private lease of the currently paired
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
key: open **Coding agent → Connection → ChatGPT accounts**, choose **Continue with ChatGPT**,
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
**Remember credentials on this computer** is selected (the default); Unix protects these files
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

Sending accepts a durable client message ID before starting a turn. Retries and
reloads reuse that identity, and ordered draft revisions prevent delayed saves
from restoring sent text. While the agent works, Send queues the follow-up for
automatic dispatch after its response. **Steer now** applies guidance at the next
safe operation boundary. Unstarted tool calls receive skipped results so native
provider history remains valid. The queue supports editing, reordering and removal,
including during generation, with limits of 16 messages, 100,000 characters per
message and 200,000 total characters. Queue text is excluded from provider context
and transcript exports until accepted. Invalid run arguments and stale queue reviews
leave the message queued. Automatic follow-ups share the current run's limits and
permission lease. **Permissions → Sending** offers manual dispatch and review
before every run; manual `RunQueuedAsync` checks the selected ID and queue revision.
The workbench preserves its separate composer draft. Run review shows the captured
task, provider, model, request, permissions and limits. Every Full Access run
requires a fresh acknowledgement. Stop preserves completed operations and pauses
the thread; Resume continues without replaying them.
Request timeout, retry count and tool-result size are independently editable.

Enter sends or queues; Ctrl/Cmd+Enter steers an active turn. Escape stops work.
Shift+Enter inserts a line and IME composition never submits. The composer's
Code/Plan picker separates read-only investigation from implementation. A completed
plan has a revision-checked **Implement plan** handoff. `/goal <objective>` or
**Set a goal** creates an explicit persistent objective with optional token budget;
pause, resume and clear are user controls. Goal completion requires evidence, and
automatic continuation stops for planning, exhausted budgets or no tool progress.
Restored goals wait for explicit resume. Completed messages
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

## Historical companion candidate acceptance

The following evidence concerns the earlier companion candidate, before the direct
browser agents and document/tool docking expansion. It does not establish acceptance
for the current implementation. On 2026-10-08,
the complete native solution and integrated Playground built with warnings treated
as errors.
The native suite passes all 2,067 tests after merging `8ebd279`:
280 core, 169 Tooling, 1,356 Avalonia, 154 automation/MCP/agent, 94 language-server
and 14 workspace tests. This includes 42 official-SDK provider transport cases.
The expanded behavioral coverage and package acceptance listed below also pass.
Native, upstream, theme and package evidence uses `b5a61f0`. The subsequent `6fd67a3`
changes only Playground source-selection focus, its browser regression and this
ledger; the reusable libraries and companion are unchanged. The Playground publish,
nine asset tests, 12 focused browser regressions and full browser run use `6fd67a3`.

Fifty additional native agent cases now cover exact block restoration and complete
patch application/reversal with Git, empty/added/deleted documents, UTF-16 and mixed
line endings, bounded/coarse diff display, task-start/latest-run checkpoints,
comparison/content identities, stale source guards, queue preparation races and
workspace replacement. Compaction cases preserve requirements, plans and complete
native turns; reject malformed, oversized, output-limited or unfittable summaries;
regenerate only unexecuted batches; and share request/token budgets. Lifecycle cases
exercise scoped run grants, account cancellation during approval/tools, retry
deadlines, bounded tool results and serialized run/compaction admission.

These tests fixed diff displays exceeding their aggregate budget, invalid empty-file
patches, unfinished requests displacing retained complete turns, and model-window
checks missing from checkpoint requests or disabled with the input threshold.
An unfinished request now remains the final prompt independently of retained turns.
Late provider success after revocation or request timeout no longer completes a task;
reported usage is counted once and streamed public text remains incomplete.

Browser restoration exposed editor captures silently normalizing mixed line endings.
The editor now retains exact source independently of Monaco/textarea display buffers,
applies user changes to source ranges and translates selections, navigation and Roslyn
queries to actual source offsets. Explicit EOL changes still convert the document.
Nine asset tests pass, including six new cases for source-position mapping, Unicode/BOM,
multiple edits, newline insertion/deletion, fallback input and authoritative replacement.

The compiler/Roslyn pass adds 43 native cases. Operation and flow inspection covers
initializers, calls, conversions, lifted operators, branches/finally regions,
captures, nested local functions/lambdas, UTF-16 statement ranges, generated
provenance, serialization, cancellation and truncation. Navigation covers partial
and generated declarations, outline nesting, aliases, constructed generics,
metadata bases, explicit implementations, overrides and hidden/reimplemented
members. Source actions compile and execute the edited source, checking return
values, nullable types, overload selection, ref/void/throw bodies and comments/CRLF.
Settings tests exercise conditional compilation and language versions, diagnostic
policies, unsafe/overflow behavior, entry points, platform and deterministic emission,
along with validation and immutable collection snapshots.

These tests found nested flow graphs trying to resolve a parent graph's region as a
local ID, and interface navigation omitting overrides of an inherited implementation.
Both are fixed. Expression-to-block actions now allow trailing declaration comments
that lie outside the replaced source span, preserving those comments and line endings.

The coordinated rename validation adds 30 cases covering cross-file XAML/C# edits,
generated fields/loaders, namespaces, interface/override and record contracts,
registered/attached properties, routed events, compiled bindings and XML entities.
The Avalonia cases compile and execute the renamed project and verify live values
and event delivery. Validation found and fixed source-assembly identity mapping,
silent capture of an outer generic parameter, and source ranges for generic type
arguments, markup names/values and compiled paths. Encoded names/values and quoted
arguments retain complete raw XML ranges. The source-restore fixture now checks
both stale comparison and stale workspace guards and refreshes before restoration.

Runtime validation adds 31 tests against actual Avalonia controls and peers. They
exercise keyboard/text editing, clicks, mouse/touch capture, wheel input, contact
limits and reset; accessibility invoke/toggle/value/range/selection/scroll actions,
virtual peer paging and relationships; and reference arguments and returned arrays.
Object cases cover explicit interfaces, typed and read-only dictionaries, retained
identity, independent absolute expiry, the 512-handle limit, GC release, cancelled
or detached asynchronous results, observer limits/coalescing and cleanup. An additive
constructor accepts the host's `TimeProvider` for deterministic lease expiry; the
existing constructor continues to use the system clock.

These tests exposed touch capture being checked against the mouse pointer: valid
touch drags crossing the preview boundary were rejected, while capture moved outside
the preview could still receive events. Each contact now checks its own capture and
resets input if that capture leaves the inspected tree. A mouse capture cannot admit
an unrelated touch outside the preview. Tests also exposed nested struct edits that
only changed a boxed copy. Member edits now write back through writable value-type
owners, including array/dictionary/nullable slots, and return the actual stored value
after owner setters run. Read-only value owners reject edits before mutation;
reference-valued children and stored boxes remain editable without replacing them.

Designer validation adds 41 native cases: geometry alignment/distribution and group
transforms, actual compiled-source layout round trips, path-aware selection,
multi-document atomic edits/Undo, stale plans, min/max constraints, cancellation
and pointer/keyboard gestures on the real Avalonia surface. The round trips cover
all horizontal/vertical alignments, Canvas anchors/margins and both StackPanel
orientations. Validation exposed incorrect margin compensation and opposite-Canvas
anchor resizing, a zero-size aspect-lock calculation, unreachable resize handles
outside the selected control, and gestures publishing after runtime layout changed.
Those cases are fixed and pass.

The current Pages-origin browser pass succeeds in all 16 selected automation,
designer and runtime scenarios, including eight new cases. It serves the candidate
at the production HTTPS origin and `/XamlG/` base path while using real companion
HTTP/WebSocket traffic. MCP cases cover reviewed geometry, independent revision
conflicts, constraints, path selection, main/template-resource edits and one-step
Undo, shared-template rejection, explicit reload and read-only/revoked policies.
Actual browser gestures cover group drag, resize and cancellation. Runtime cases
exercise keyboard/pointer/touch input, accessibility providers and returned
selection arrays, explicit interfaces, reference arguments, typed dictionaries,
nested structs, observers, retained handles and retirement after preview replacement.

Owner-only UI cases exercise designer plan/review/apply/Undo/conflicts and runtime
property/input/accessibility/export operations with remote sharing disabled. Reload
changes the inspected handles, preserves unchanged input declarations and applies
changed declarations. These cases exposed a refresh rejected while the host was
compiling but still marked as observed; the workbenches now defer refresh until the
host is idle and retain failed refreshes for retry. Action fields update while
typing, and coordinate/dimension fields have independent labels. The Pages fixture
bounds simultaneous local asset reads, and its transport-only companion disables
account mode so it cannot access or lock the developer's credential store.

All six existing C# authoring/project browser scenarios also pass at the Pages
origin. They cover cross-file completion/definitions/references, semantic actions
and rename/Undo, owner Monaco commands with sharing disabled, generated-field
rename into XAML, generated-file selection, multi-file compilation and runtime
handlers, draft restoration, and C# file move/remove/Undo. The fixtures now enable
access after example selection retires the old workspace and check version-4
exports, including consistency between the compiler settings document and options.

Eight new C#/compiler browser scenarios also pass at the Pages origin, bringing
the focused authoring/compiler coverage to 14 cases. Real HTTP MCP calls verify
conditional code in a running Avalonia control, diagnostic policies, effective
reference selection (including no references), generated tree identities, atomic
settings validation/revision conflicts, and version-4 exports. Non-browser output
emits an actual AMD64 executable while rejecting preview execution. Owner-only
compiler controls exercise save/Undo/Redo, persisted and older drafts, conflicting
draft preservation, Roslyn inspection and result export. Undo exposed the compiler
workbench marking a revision observed before a rejected refresh; it now waits for
the host to be idle and records only the successfully received snapshot.

MCP C# cases inspect nested control-flow graphs, statement data flow, operation
trees and generated code, declaration outlines/search, type hierarchies, constructed
generic type definitions and interface overrides. Owner Monaco type/implementation
navigation, outline selection and semantic action/Undo work with sharing disabled.
The rename UI rejects a stale preview, then updates C#/XAML across three files as one
Undo transaction, preserves unrelated edits, and compiles/runs the renamed project.
Browser helpers account for virtualized outline rows and read the actual editor
instead of a cached navigation model for the same document.

Thirty selected agent, authoring, editor and resource browser scenarios now pass at
the Pages origin. The three official-provider workbenches exercise Markdown rendering,
IME/Shift+Enter and reviewed keyboard submission, delayed draft/queue responses,
task-start/latest-run comparisons, exact block restoration and normal project Undo,
stale restore rejection, explicit compaction and pane reopening. The source-editor
cases cover immediate captures, mixed line endings, textarea fallback, document
retirement, file moves and exact first-line navigation with a byte-order mark.
Resource exports now check version 4 and the captured compiler options.

These checks fixed keyboard resubmission being ignored while an earlier draft save
was pending, source navigation calling a tool-pane-only Dockyard method on a document,
empty Boolean ARIA selection attributes and first-line navigation columns losing the
hidden byte-order mark at the Monaco/Roslyn boundary. The keyboard regression holds
draft responses until after cancel/resubmit, so it does not depend on transport speed.

Four additional agent browser scenarios pass: independent task/pane review drafts,
feedback targets and bounded diff paging; historical thread paging, late responses,
expanded tool entries and reading position; output-limited replies and reviewed resume;
and Stop cancelling a real provider stream while retaining incomplete public text.
They also verify context handoff and that unsent drafts/feedback never enter provider
requests or public exports. Returning to a task exposed a stale review-document
picker; its DOM identity now follows the task and comparison.

Ten new MCP protocol cases pass through the official SDK and real HTTP. They cover
principal-bound task reads/updates/cancellation/subscriptions, workspace replacement,
retention limits, cleanup, bounded error results, opt-in and permission denial;
encoded resource templates, authenticated completion, read permissions, legacy and
modern exact-URI subscriptions, independent cancellation and unsubscribe/resubscribe;
and complete catalog paging, invalid/stale cursors and embedding-owned pagination.
Validation found SDK cancellation could cancel another principal's execution even
when the store declined the request. Task mutation handlers now check ownership
before invoking the SDK. Completion now carries the transport principal. The companion
enables sessions for legacy HTTP clients while keeping modern HTTP stateless.

Three integrated MCP browser scenarios now pass in focused runs. They reconstruct
and hash actual JSON/ZIP/assembly/PDB snapshots, verify immutable multi-chunk reads,
local download/release, principal ownership, revisions and workspace retirement.
They exercise modern background waits alongside source edits, legacy synchronous
waits, timeout/cancellation, owner operation controls and task subscriptions. Exact
source/generated/runtime/property resource notifications, encoded paths, completion,
ReadOnly/Ask policies and revocation use the real companion and Pages browser origin.
Three native artifact-store cases verify byte isolation, independent expiry, chunk
bounds, workspace clearing and count/byte-budget eviction.

Twenty-two deterministic account cases pass through real loopback callbacks, signed
RSA identities, the credential store and the official OpenAI SDK. They cover PKCE,
identity/callback rejection, registration retry, remembered and memory-only sessions,
store ownership/permissions, account selection and plan consent, concurrent refresh,
key rotation and temporary validation failure, sign-out races and terminal failures.
Account-bound tools continue under the captured account; sign-out during approval
prevents execution. Two Pages-origin workbench scenarios pass for sign-in without
API keys, preferences, model discovery, task isolation, consent, retry, cancellation
and sign-out during a real response stream. All credentials and inference responses
are synthetic; no production account was signed in or charged.

The complete browser suite passes all 73 scenarios in 27 files in one clean
Pages-origin run against `6fd67a3` (10.5 minutes, no retries or failures).
It uses the prepared production publish, HTTPS Pages origin and `/XamlG/` base path.

The first complete Pages-origin run passed 72 cases. Its legacy-draft fixture now
installs old storage before startup so a live editor capture cannot overwrite it;
the corrected case passes. CI also exposed a real sign-in retry race: a completed
or failed exchange was rejected until its callback listener finished cleanup.
Terminal sign-ins now retire the listener before admitting the next request.
The MCP ownership test uses separate acknowledgement/status channels because SDK
notification handlers run concurrently; raw SSE acceptance still verifies ordering.
All 27 affected account/task cases pass locally, including five consecutive focused
runs. Native/MSBuild CI passes on Linux, Windows and macOS, and package, host,
upstream and theme jobs pass for implementation candidate `b5a61f0`.

The second complete Pages-origin run passed 72 cases and exposed a designer focus
race: its asynchronous source highlight could focus Monaco during a pointer gesture,
preventing Escape from reaching Avalonia. Visual selection now reveals source without
taking focus, including deferred resource-editor reveals. Explicit source navigation
retains normal focus. The browser regression waits for the asynchronous highlight
and checks focus before pressing Escape; it reproduces the failure on the old publish.
The corrected publish passes all 12 focused designer/authoring cases and the complete
73-case browser run. Native/MSBuild CI on Linux and Windows, package, host, upstream,
theme and source-snapshot jobs also pass for `6fd67a3`; the PR reports current CI status.

The 25 MSBuild input and five fingerprint cases, all 975 pinned upstream assertions
and parity cases, and CLI process checks pass. Unmodified Simple (81 physical plus
one linked document) and Fluent (86 documents) themes compile and construct, each
realizing 34 control/theme cases. All 21 candidate packages build and pass inventory
inspection and clean external consumer execution. This includes installed CLI/LSP
and companion tools, eight LSP process suites, portable/Avalonia consumers, source
and resource emission, standalone automation/MCP/agents and all three provider SDK
adapters. Temporary consumer caches and large theme/upstream outputs are removed
after retaining their result summaries.

That candidate merged main through `8ebd279` and passed its native build and test
suite. Its integration was in the existing Playground. The candidate's local
acceptance evidence is recorded in
[PR #9](https://github.com/wieslawsoltes/XamlG/pull/9). Superseded publishes, packages,
reference clones, isolated consumer caches and temporary browser traces are removed.
Review and required CI completion precede merge; public Pages deployment follows
the existing main-only workflow.

## Implementation details and earlier validation

The notes below retain historical implementation-checkpoint details. Current
acceptance status is at the top of this document and in the feature parity audit.

Implemented since that checkpoint: tasks bind to the creating browser workspace
lifetime; replacement and revocation disconnect that lifetime. A separate preparation
stage captures source before accepting a reviewed queue entry. Source comparisons
support task-start and latest-run checkpoints. The workbench adds independent task
settings, numeric-only saved defaults, public context handoff, thread copy and reading
position, bounded unified diffs, complete replacement patch export, line-targeted
queued review feedback and explicit source-restore confirmation.

Recovery now retains full provider Retry-After deadlines, requires increased effective
output allowance after output stops on routes that accept a cap, counts unknown failed-attempt usage separately,
reserves bounded results before tool execution and bounds aggregate retained request
context. `AgentHttpHandler` carries canonical HTTP/stream error and retry advice through
the official SDK transports. Use it with injected SDK clients to retain complete
failure classification and Retry-After headers. `CompactAsync` creates a paid tool-free public checkpoint,
stages and validates replacement context, retains complete recent native turns, and
preserves original history on failure. Manual compaction is reviewed, and `/compact`
is handled locally. Automatic triggers, model-window estimates, retained turns and
checkpoint output allowance are configurable. The older synchronous `Compact` API
remains a local deterministic checkpoint for embedding compatibility.

The feature-first pass added compaction and restore confirmations, MCP templates,
completion, resource notifications, catalog pagination, background tasks, immutable
build downloads and activity/operations controls. Subsequent behavioral acceptance
covers cancellation/ownership, notification streams, artifact emission/download and
independent package consumers; the current evidence is recorded above.

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
browser UI compile. Native behavioral tests and the expanded Pages-origin runtime,
browser and MCP scenarios recorded above pass. The current acceptance section also
records account-mode, recovery, authoring, protocol and package-consumer results.

The reference provider/protocol recovery pass is now implemented, including stop/failure
classification and continuation behavior across the three SDKs. Coordinated XAML/C# rename, broader semantic source actions and symbol
navigation are now implemented below, alongside operation/control/data-flow inspection
and compiler settings. The workbench Markdown, Enter/IME, independent review state,
source navigation, thread paging and selective block restoration are also implemented
and covered by the native and browser acceptance described above. Earlier test
counts below describe their implementation checkpoints. The published Pages app
receives this integration only after the PR reaches main.

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
member paging and release controls. Native acceptance now verifies GC retention and
release, absolute expiry, origin removal, explicit interfaces, peer arguments and
returned selection arrays. The current browser/MCP cases also verify explicit
interfaces, reference arguments, retained selection arrays, release and preview
replacement through the real companion.

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
runtime changes. Library/browser builds and the current Pages-origin designer
scenarios pass for group gestures, resource-template provenance, one-step Undo,
source/runtime conflicts, constraints, cancellation and owner/remote permissions.
Native surface and compiler-backed planner tests also cover
group movement/nudging, multi-document Undo, stale plans, constraints, cancellation
and the standard layout-policy cases recorded above. The default policy emits Canvas offsets or
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

Current compiler settings and flow-inspection acceptance is recorded in the local
evidence section above, including the nested graph and workbench refresh fixes.

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

Targeted OpenAI-package, companion and Playground builds pass. Deterministic
OAuth/provider/browser coverage now exercises identity and callback rejection,
registration retry, stored-session locking/permissions, token rotation/sign-out races,
account-bound tools, consent, model catalogs, namespace continuations, errors and UI
isolation. The current acceptance section records the results. No actual account
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
and the JavaScript syntax check passes. Subsequent native and browser acceptance
covers exact block reconstruction and Undo, stale/project/checkpoint races, unchanged
feedback targets, task/pane isolation, late responses, source navigation, inert
Markdown, keyboard/IME, scrolling/selection, thread paging and incomplete replies.
Provider responses in acceptance are synthetic and use the official SDK transports.

The reusable C# language service now supplies document outlines, declaration search,
namespace/type member inspection, type definitions, type hierarchies and implementation
navigation. MCP exposes all six operations; Monaco adds outlines, type definitions and
implementations alongside its existing completion, hover and definition/reference
providers. Hierarchies include metadata bases and source/generated derived types.
Result count, outline depth, inspected syntax and display text are bounded, with
explicit truncation. Locations retain editable/generated provenance and exact spans.

Source actions now cover explicit/target-typed creation, type/member qualification,
predefined type names, constant-preserving `nameof`, and method/property expression
or block bodies in addition to local type inference. Controlled syntax rewrites are
compiled against the actual project; their types, constants, selected symbols and
surviving identifier bindings are checked before an action is offered. Comments and
directives constrain available rewrites. Applying an action still recomputes it against
the expected revision and uses one project undo transaction. This does not introduce
the desktop Roslyn workspace's general analyzer/code-fix catalog or broaden rename yet.

Current navigation/action acceptance is recorded in the local evidence section
above, including native execution of rewritten code and generic-interface dispatch.

Coordinated rename is now implemented in the reusable `XamlProjectRenameService` and
shared by the existing XAML/C# editor dialog and MCP, including an XAML preview endpoint.
The C# planner follows source interface/override contracts and record positional-member
links; namespaces, aliases, labels and query variables can retain declaration identities
through edits. The project planner updates compiler-resolved XAML type/member/name
references, code-behind class names, namespace URIs, matching end tags, static/enum
values and compiled bindings, then regenerates all XamlG-owned sources and loader adapters.
Binding verification maps source declarations and generated member identities instead
of reusing generated-file offsets. It checks source identifiers, XAML symbols,
constructors/factories, implicit content and member getter/setter identities. Source
position mapping uses sorted cumulative deltas; XAML owner lookups use ordered syntax
positions and parents. No files or running controls are changed while planning.

Avalonia properties/events additionally link their registration field, CLR wrapper and
attached accessors. Different linked declarations retain their required prefixes/suffixes.
The planner recognizes the actual registration type and compiler-resolved Register call;
literal names are edited and `nameof` follows normal symbol edits. XAML selections show
the logical name. External contracts, shared/custom registration factories and unresolved
reflection/string references are explicit boundaries; the latter are not treated as
statically resolved references. A failed candidate compilation or binding check rejects
the complete transaction. Existing x:Name/template scopes retain their specialized
reference index and now receive the project-level regeneration/binding checks too.

The compiler now supplies more precise type/markup/generic-argument source spans,
including raw XML entity mapping, and records attached-event member references.
Existing public type-resolution signatures remain available, with new source-aware
entry points. Targeted Tooling and integrated Playground builds pass without warnings
or errors. The old source-interface rejection fixture now expects coordinated edits.
Subsequent behavioral acceptance covers cross-file and cross-language renames,
contracts/generics/records, registered and attached members, namespace/class changes,
XML entities and markup/type arguments, generated adapters, implicit content/accessors,
binding capture, stale previews and one-step undo.

The provider recovery pass adds shared exact-code classification for account access,
safety, context, quota, authentication, request, rate and temporary server failures.
Quota and repairable configuration errors pause without automatic retries; safety
rejections terminate the task. Provider messages never become exception text or
transcript diagnostics. Bounded HTTP error reads retain Retry-After deadlines.
Reported usage survives failed generations, malformed continuations and rejected
stops. OpenAI distinguishes actual max-output stops from filtered or failed responses,
rejects contradictory terminal events and skips truncated function JSON. Anthropic
checks block/message ordering and maps its typed streaming errors. Gemini preserves
reported cumulative usage and distinguishes safety, invalid calls and tool-call stops.

The shared HTTP handler also checks SSE error frames with .NET's streaming parser
before an SDK converter can discard them. Normal frames still go through the official
SDK and retain native reasoning/signature content. The stream is bounded to 16 MiB;
one frame is delivered at a time so a later error cannot hide earlier usage. This
addresses a reproduced error-frame omission in Google.GenAI 1.24.0. Without this
handler, the Gemini adapter rejects an empty converted error frame before tools run,
but cannot recover error fields the SDK erased. Embedders should use the shared
handler, as the companion does, for complete classification.

ChatGPT account-mode output stops pause for explicit reviewed resume without requiring
an unsupported higher output cap. API-key routes still require a larger effective
allowance. In both cases the original native context is retained and partial tools
are discarded. Forty-two provider discovery, continuation and recovery tests now pass
through the pinned official SDKs, including quota priority, typed stream failures,
failed-attempt usage, encrypted/signed continuation preservation, late-error rejection,
and output-limit resume with and without a supported cap. These fixtures do not sign
into a real account or request production inference. The current acceptance section
records the full native/MCP/OAuth, Pages-origin, package-consumer and CI results.


## Durable sessions and focused requests

Studio saves projects, auxiliary sources, compiler settings, workspace undo/redo,
document layout, editor positions, inspector inputs, agent drafts, queues, plans,
reviews, usage, native provider continuations and connection preferences. Browser
records use IndexedDB, AES-GCM with a nonextractable browser key, compression and
atomic current/previous versions. This protects the stored representation; scripts
running on the same origin can still use the key. Public source/transcript exports
never include saved credentials or private native reasoning/signatures.

Storage failures are shown in Studio. Concurrent tabs cannot overwrite revisions
loaded by another tab. A damaged record recovers its previous complete version
with a visible notice. Forgetting a connection also removes its previous version.
Permission grants and pending approvals expire on reload. Interrupted tasks return
paused; unconsumed tool calls receive explicit recovery results and are never
replayed automatically. Review and resume against the current project and preview.

The companion stores its agent session and stable owner/MCP tokens in an exclusive
private directory, using owner-only Unix permissions or current-user DPAPI on
Windows. Override its location with `--agent-store=PATH`. Sessions are tied to the
saved project identity; connecting a different project retires those tasks.

Provider requests use a focused initial catalog and `xamlg_agent_tools` to discover
and enable additional schemas. All IDE capabilities remain available, and discovery
does not grant permissions. **Send every tool schema with every request** restores
the full-catalog behavior. Unchanged agent state polls return a session/revision
cursor; streamed text is coalesced for display and only completed text is retained
in durable history. Provider speed still depends on the chosen model and network.

## Preview computer tools

`xamlg_computer_observe` captures a PNG of the running trusted or isolated preview,
plus a bounded semantic target list, focus, frame ID, revision and image-to-DIP
mapping. MCP receives a native image content block. OpenAI, Anthropic and Gemini
receive native image input, and the conversation displays a screenshot instead of
base64 JSON. The OpenAI adapter uses the pinned SDK's wire patch for the API's
[image-bearing function outputs](https://developers.openai.com/api/docs/guides/function-calling).

`xamlg_computer_actions` accepts up to 32 click, double-click, move, down/up, scroll,
drag, key, text, focus, touch, bounded wait, assert or reset actions. Targets can be
coordinates or object/name/automation-ID/text selectors. Coordinates use the
observed image by default; keys/text without a selector use current focus. Actions
require a current frame and revision, stop at the first failure, report completed
indices and return a fresh observation. Never retry a partial batch blindly.

For live clocks or unrelated progress updates, opt into `refreshTargets: true`.
Keep the original frame ID/revision and give every action except wait/reset an
explicit selector from that frame's element page. Before any input, the inspector
checks the viewport, tree topology, target and visual ancestor identities,
DataContext references, names, text, enabled/visible state, hit-test visibility,
focusability and opacity. It pins each resolved control for the batch and checks
current pointer/touch hits, so a covering sibling cannot receive its input.
Coordinates, drag paths, unobserved controls and implicit keyboard focus require
a fresh strict frame. Release held input first or start the refreshed batch with
reset. This option tolerates unrelated property updates; it does not freeze the
application or deep-copy mutable business data. Earlier actions may deliberately
change later targets within the same batch.

`xamlg_computer_viewport` sets a 128–4096 DIP viewport for responsive testing;
omitting both dimensions returns to automatic dock sizing. Observe again after a
resize or application change. Runtime edits and input remain subject to the active
permission profile, and computer actions leave visual design mode.
