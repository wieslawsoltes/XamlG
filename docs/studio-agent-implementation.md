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
companion. A configured `XAMLG_STUDIO_TOKEN` must contain at least 32 characters;
otherwise the host generates one. Sharing begins disabled and disconnecting
revokes in-flight operations. Browser sharing policy and agent run policy both
apply. Runtime actions execute the application running in the preview.

Set `OPENAI_API_KEY` in the companion environment to enable the current official
OpenAI SDK adapter, then use **Coding agent** to discover models or enter a model
ID. Provider credentials are not sent to browser code, transcript exports or MCP
clients. `OPENAI_ENDPOINT` optionally supplies an HTTPS gateway or an HTTP
loopback endpoint for deterministic protocol testing. The official SDK currently
marks Responses APIs with `OPENAI001`; the adapter suppresses that diagnostic.

External MCP clients use authenticated Streamable HTTP at `/mcp`, or launch the
companion with `--stdio=true`. With stdio, the loopback browser bridge remains
available and host logs go to stderr. Add an exact browser origin with
`--origins=http://127.0.0.1:8765` when serving the IDE separately. HTTP Host and
Origin checks and the local pairing token apply independently of cloud credentials.

`XamlG.Automation` has no IDE dependency. Implement `IAutomationHost` or build an
`AutomationCatalog`, add typed handlers, and use `.WithAutomation(host)` on the
MCP SDK builder. Supply the same host and an `IAgentProvider` to `AgentHarness`.
Optional `IAgentWorkspace` support supplies bounded before/after checkpoints and
selective source restoration. `scripts/test-studio-packages.py` is an executable
consumer example that uses only NuGet references outside this repository.

## Local evidence and remaining work

- 462 real-Avalonia tests passed, including eight runtime inspector tests.
- 18 automation/MCP/agent tests passed, including actual official MCP and OpenAI
  SDK transports, permission enforcement, native tool continuation, no replay on
  resume, queued messages, compaction and conflict-checked source restoration.
- The five new packages built, installed and ran through a separate consumer
  with an initially empty package cache. Its temporary files are removed even
  when validation fails.
- The full browser run passed 34 of 35 scenarios. The remaining Dockyard
  floating/docking/layout-restore case passed after correcting the API call for
  the pinned package. This covers actual rendering, authoring, designer gestures,
  source undo, file moves, editor lifetimes, isolation, mobile layout and HTTP MCP.
- The workbench test uses a local Responses fixture through the official SDK and
  the real companion/browser. It edits XAML, compiles, reviews and selectively
  restores source, compacts context and exports the public thread. No paid
  provider account was used. CI starts and removes its own companion processes.

Full reference parity remains in progress: additional official provider adapters
and account-mode support; MCP subscriptions/notifications and session routing;
more general runtime object/tree/binding/style operations; a full multi-file C#
workspace and Roslyn authoring surface; and the corresponding UI and protocol
acceptance coverage. This ledger does not claim those capabilities from a build
or from the existence of a tool name.

Temporary reference clones and superseded publishes are removed when no longer
needed. Package consumer caches are scoped to temporary directories. Large failed
browser traces should be discarded after diagnosis, while small acceptance logs
and representative screenshots can be retained for review.
