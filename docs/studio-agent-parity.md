# Coding-agent parity and docking acceptance

Reference: [VB6 at `3b7ab056221da7d0b2b586596ab555256d68edba`](https://github.com/wieslawsoltes/VB6/tree/3b7ab056221da7d0b2b586596ab555256d68edba),
reviewed again on 2026-10-08 against `docs/coding-agents.md`, the four
`CODING-AGENT-*` guides, `CHATGPT-LOGIN.md`, and the corresponding implementation
and acceptance tests. This concerns the coding-agent workflows, adapted to XAML,
C# and Avalonia; it does not request a VB6 language/runtime implementation.

The preceding companion-mode test pass did **not** establish full parity. The
expanded implementation below requires native, browser, package and current-head
CI acceptance before merge. [PR #9](https://github.com/wieslawsoltes/XamlG/pull/9)
records the current acceptance, merge and deployment evidence. The paired companion
continues to enforce its browser sharing policy; direct and provider-relay runs
have independent browser-owned authority.

| Requirement | Implementation | Acceptance requirement |
| --- | --- | --- |
| Browser API-key agents without a companion | Implemented | Run the shared C# harness and official OpenAI, Anthropic and Gemini SDK adapters in the Pages app; no bridge or MCP sharing prerequisite. |
| Local provider relay | Implemented | Browser-owned harness with owner-authenticated, bounded provider transport; provider keys stay in the host; no MCP pairing required. |
| Direct credential lifecycle | Implemented | Password input, explicit browser-exposure consent, remembered per-provider credentials by default, explicit Forget connection, live-client cancellation on closure/switching, and exclusion from public exports and localStorage. |
| Model discovery and manual model IDs | Implemented | Both transports, selected-provider credentials, bounded discovery, no source disclosure from discovery. |
| Independent coding-agent authority | Implemented | Direct owner adapter with its own run permissions; external MCP grants and sharing remain independent. |
| Reusable workbench session | Implemented | Share task/queue/review/approval orchestration between browser and companion. |
| Provider-native continuations and streaming | Implemented | Exercise all three official SDKs in browser direct mode, preserving opaque native state and exposing only public text. |
| Named tasks, drafts, rename/delete and context handoff | Implemented | Complete task-management UI, deletion confirmation, task/billing-mode identity, separate saved provider credentials and independent durable drafts. |
| Task plans and questions | Implemented | Keep model-reported plan status distinct from validation; cancel/late-answer and workspace retirement in direct mode. |
| Permission profiles, exact/scope rules and Never ask | Implemented | Structured scope/tool rule controls, validation against the real catalog, combined-effect denials and destructive Auto edit review. |
| Immutable embedding restrictions and full-access acknowledgement | Implemented | Host ceiling, allowed modes, denied scopes/tools, lease ceiling and run-approval policy enforced by the reusable engine. |
| Active grants, selected revocation and lease status | Implemented | Inspect remembered exact-tool grants, revoke individual grants, show active expiry separately from selected preferences, revoke and stop. |
| Tool catalog inside Coding agent | Implemented | Search/filter actual schemas and effect metadata independently of MCP access. |
| Activity and exports | Implemented | Dedicated bounded activity view and complete transcript/review exports, with credentials/native signatures excluded. |
| Modern workbench navigation | Implemented | Conversation, tasks, connection, plan, changes, queue, permissions, tools and activity with compact responsive navigation. |
| Threads and rendering | Implemented | Preserve Markdown/copy, tool expansion, waiting/streaming/partial states, Enter/IME, reading position and bounded paging in both modes. |
| Limits, recovery and compaction | Implemented | Keep presets, cumulative accounting, failed usage, no-replay resume, retry deadlines and atomic checkpoints in direct browser execution. |
| Queued follow-ups | Implemented | Automatic next-turn dispatch, safe-boundary steering, optimistic queue revisions, editable/reorderable queue, optional manual dispatch and task/workspace isolation. |
| Changes and selective restoration | Implemented | Preserve task/run baselines, exact patch/block restoration, stale guards, feedback, source navigation and normal Undo with document tabs. |
| ChatGPT account mode | Implemented | Preserve sign-in, account binding, consent, persistence choices and sign-out; direct API mode must not silently use account billing. |
| Coding agent as a Dockyard tool | Implemented | Proper close/reopen/floating lifecycle and modern contents; closing cancels work and clears live SDK clients while preserving saved connections. |
| Agent access as a Dockyard tool | Implemented | Dockable/floating/closeable/restorable access pane retaining MCP permissions, capabilities, operations and artifacts. |
| Individual inspector Dockyard tools | Implemented | Every inspector gets its own stable tool identity, activation, floating, close/reopen and persisted layout, including the nine runtime inspection sections. |
| Source-file Dockyard documents | Implemented | Separate main XAML/C#, auxiliary C# and resource documents; navigation activates the correct file, moves/splits preserve edits, closing preserves project source. |
| Application shell | Implemented | Compact Project/Edit/Run/View/Tools menus, keyboard dismissal/navigation, responsive controls and theme support; Run opens the preview pane. |
| Workspace/layout lifecycle | Implemented | Migrate obsolete saved layouts; reconcile added, removed and renamed documents; restore tool/document identities without serializing credentials or source into layout. |
| Local Release preview | Release publish and companion host | Validate the expanded local publish and leave it running for user testing. |
| CI and merge | Existing PR and main-only Pages workflow | Run expanded acceptance, require passing current-head CI before merge, then verify deployment. |

Full validation follows the feature implementation. Targeted builds and checks may
be used during implementation to resolve integration failures. Do not infer parity
from a tool count or from the earlier 73-case browser pass.

Recorded local evidence on 2026-10-08: 2,071 native tests, including 158 agent/automation tests;
17 document, runtime and editor browser regressions; three browser direct SDK
scenarios with source approval, compilation and native tool continuation; three
local provider-relay scenarios with model discovery, streamed responses, origin
and owner-token checks, and no MCP pairing. All pass with synthetic credentials.
All 21 shipping packages also pass inventory and clean consumer acceptance, including
CLI/LSP/companion installation and standalone automation/MCP/agent use. Package and
native evidence uses `b602ab3`; the following layout fix changes browser JavaScript
only. Full UI and current-head CI results are recorded in the PR.

## Codex-style thread workflows (2026-10-10)

[PR #21](https://github.com/wieslawsoltes/XamlG/pull/21) adds explicit collaboration
mode and goal state to the shared browser/companion harness. Plan mode exposes
inspection tools only, enforces every declared tool effect, saves a proposed plan,
and accepts that exact revision when the user chooses **Implement plan**. Ordinary
requests do not create goals. `/goal <objective>` starts a persistent goal;
`/goal pause`, `/goal resume` and `/goal clear` control it. Goals retain token
accounting and verification evidence across reloads. Reloaded goals wait for an
explicit resume. Planning and turns without tool progress cannot spin an automatic
continuation loop.

**Send** accepts a message with a durable client identity. During work it becomes
a queued follow-up; **Steer now** applies guidance at a safe boundary and closes
unstarted tool calls with explicit skipped results. Automatic follow-ups and goal
continuations share the active run's limits and permission lease. Stop retains
completed results and makes the thread resumable. Delayed draft writes cannot
restore already-sent text. Manual dispatch and a review before every run remain
available in **Permissions → Sending**; Full Access still requires acknowledgement.

The conversation adds a mode picker, proposed-plan handoff, goal status, inline
queue, resume state and a responsive thread sidebar. This is an ongoing overhaul;
native workflow tests and a successful UI build do not establish complete visual
or provider parity. Current browser and CI evidence belongs in the PR.
