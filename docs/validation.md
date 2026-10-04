# Validation and provenance

SDK/package versions are pinned in `global.json` and `Directory.Packages.props`. Upstream source pins live in `tests/Upstream.props`: XamlX `7ef6aef496ab6e8dcf3df04bef697be49db37c04` and Avalonia `17350180c33b063f0e98abbfd19aa3cae63f5d56`. Runtime packages and inspected source are tracked independently. Reading an implementation is not evidence of passing its tests.

## Local solution and parallel worktrees

`XamlG.slnx` includes compiler/generator execution, tooling/designer, workspace/watch, protocol/publication and real-Avalonia tests. Linux, Windows and macOS build with warnings treated as errors and execute the solution. Exact revision counts come from TRX artifacts rather than a stale static total.

`worktree-validation.yml` additionally splits validation into independent compiler/Avalonia, designer/tooling and workspace/protocol jobs. Each creates a detached Git worktree at its checked-out revision, uses separate build directories, writes separate test artifacts and removes its worktree afterward. This is parallel execution of validation jobs; it is not evidence of autonomous parallel coding agents.

Compiler tests emit and execute C#. Syntax differential tests compare incremental and full parses, including malformed edits. Runtime tests exercise detached replacement, state transfer, publication rollback, failed construction and cleanup. Tooling tests cover source transactions, namespace-safe moves, visual edits, resource document budgets and typed-expression inspection. Avalonia tests exercise selectors, themes, compiled bindings, commands/indexers, interaction state and realized visual provenance.

The parser reparses eligible subtrees and retains unaffected positioned nodes; it is not a complete position-independent red/green tree. Parser and project compiler work counters do not establish whole-pipeline latency or allocation superiority over XamlX.

## Resource linker coverage

Resource tests compile and execute relative, root-relative and referenced-assembly includes, style includes, eager merges, local/theme precedence, fresh instances and session ownership. Separate assemblies deliberately share a logical filename to check factory identity isolation. Tests also verify unresolved/dynamic sources, duplicate identities, incorrect include types, cycles, dependency failures and recovery.

Incrementality assertions inspect actual work counts: unchanged documents reuse binding/output, a value-only edit can compile one document, export signature changes invalidate assumptions, and dependency diagnostics do not poison cached bindings. Cancellation/explicit cache clearing and statement-emitting resource factories inside expression-only delegates are covered.

Workspace tests coalesce identical duplicate MSBuild inputs and reject conflicting buffers. `test-lsp-resources.py` launches the actual trusted-project stdio process, introduces an unsaved dependency error, verifies the unchanged caller's diagnostics/inspection, closes the dependency and checks recovery. It does not modify the caller version to manufacture freshness.

## Pinned upstream comparison

The original XamlX baseline executes 222 pinned cases. The XamlG adapter executes 217 runtime/diagnostic cases through XamlG binding, generated C#, Roslyn emission and runtime execution. CI checks exact executed counts and rejects skips. Four XamlX AST-shape assertions and one IL-helper-name assertion are explicitly outside the source-backend comparison. See [the harness boundary](upstream-validation.md).

XamlX is test-only, not a production dependency. These cases do not certify every framework extension, version, custom markup extension, binary-loader ABI or visual designer operation.

## Host, browser and package gates

CLI smoke tests emit assemblies in standalone and evaluated-project modes. LSP tests exchange real framed messages, edit C# inputs and verify automatic refresh; library tests exercise queued cancellation, write failures, deadlines, project/buffer-set freshness and clean shutdown.

Release validation builds 13 shipping packages, validates analyzer/runtime dependency layout, installs CLI/LSP into a clean tool environment, and exercises actual portable and Avalonia package consumers. The Avalonia consumer compiles three XAML files with linked dictionary/style factories, checks assembly resource exports, compiled/two-way bindings, static resources, typed styles and subscription retirement.

Browser tests execute the production compiler/Avalonia view, inspect source and visuals, verify immediate edits, undo/redo, mobile themes, designer gestures, isolated execution/reset, and multi-document resource projects. Resource tests include before-debounce edits, complete export, draft restoration, isolated assembly execution and missing dependencies. Pages verifies the exact public build identity and reruns the browser suite against the public URL.

PR validation creates candidate artifacts and provenance, not a NuGet publication or version-tagged GitHub release. A workflow's successful conclusion and artifacts prove that specific revision; a workflow definition alone does not.
