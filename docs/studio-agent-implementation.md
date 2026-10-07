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
is `5f26615` (including structured/collection literals and enum conversions).
The original checkout contains unrelated local compiler edits and is left intact.
The locally available pinned SDK is `/tmp/xamlg-dotnet-10.0.401/dotnet`.

## Run the current implementation

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

External MCP clients use authenticated Streamable HTTP at `/mcp`, or launch the
companion with `--stdio=true`. With stdio, the loopback browser bridge remains
available and host logs go to stderr. Add an exact browser origin with
`--origins=http://127.0.0.1:8765` when serving the IDE separately. HTTP Host and
Origin checks and the separate local tokens apply independently of cloud credentials.

`XamlG.Automation` has no IDE dependency. Implement `IAutomationHost` or build an
`AutomationCatalog`, add typed handlers, and use `.WithAutomation(host)` on the
MCP SDK builder. Supply the same host and an `IAgentProvider` to `AgentHarness`.
Optional `IAgentWorkspace` support supplies bounded before/after checkpoints and
selective source restoration. `scripts/test-studio-packages.py` is an executable
consumer example that uses only NuGet references outside this repository.

## Local evidence and remaining work

- The merged native solution passed 1,311 tests, including 845 real-Avalonia tests
  and the runtime inspector's fifteen tests for live manipulation and source
  provenance for objects and deferred resource keys.
- 37 automation/MCP/agent tests passed, including actual official MCP, OpenAI,
  Anthropic and Gemini
  SDK transports, permission enforcement, native tool continuation, no replay on
  resume, queued messages, compaction and conflict-checked source restoration.
  WebSocket tests cover concurrent admission limits, cancellation, expired owner
  leases and pending calls rejected when their browser disconnects.
- All 21 shipping packages built and passed the release-consumer suite at
  `cf9b8b9`, with consumer fixture updates at `fa15505`. The seven new packages
  built, installed and ran through a separate consumer with an initially empty
  package cache. Temporary caches and the 30 MiB candidate package set were
  removed after validation; the small inventory and logs are retained locally.
- All 38 browser scenarios passed locally after the main merge and provider expansion. The runtime
  browser scenario constructs a C# DataContext, invokes its method,
  installs and updates a real binding, inspects style value frames, and creates,
  reparents and removes controls without changing source. Coverage includes actual rendering, authoring, designer gestures,
  source undo, file moves, editor lifetimes, isolation, mobile layout and HTTP MCP.
- Workbench browser tests use local provider fixtures through all three official
  SDKs and the real companion/browser. Each discovers models, edits XAML, compiles, reviews and selectively
  restores source, compacts context and exports the public thread. No paid
  provider account was used. CI starts and removes its own companion processes.
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

Runtime tools now include bounded object-path inspection and mutation, loaded type
discovery, public method invocation with Task/ValueTask results, live control tree
changes, event watches, binding expressions and style/value-frame diagnostics.
The browser compiler emits Avalonia's source metadata; `xamlg_runtime_source`
exposes exact object and resource-key locations alongside generated-object source
identities. Keyed location reads do not instantiate deferred resources.
Template-owned visuals require template/source edits; tree operations reject
unsupported ownership. Diagnostic frame metadata uses the loaded Avalonia runtime.

Full reference parity remains in progress: account-mode support and additional
provider recovery/limit controls; MCP subscriptions/notifications and session routing;
typed runtime input and additional designer/runtime UI; the remaining Roslyn
authoring surface for the multi-file C# workspace; and the corresponding UI and protocol
acceptance coverage. This ledger does not claim those capabilities from a build
or from the existence of a tool name.

Temporary reference clones and superseded publishes are removed when no longer
needed. Package consumer caches are scoped to temporary directories. Large failed
browser traces should be discarded after diagnosis, while small acceptance logs
and representative screenshots can be retained for review.
