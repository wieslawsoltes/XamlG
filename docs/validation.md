# Validation and provenance

SDK and package versions are pinned in `global.json` and `Directory.Packages.props`. `tests/Upstream.props` records the XamlX comparison revision and the earlier Avalonia source-inspection revision. The separate unmodified-theme gate pins Avalonia `8eeda4f6f546165b3f72e63c9f42247abb306905`. Runtime packages, source inspection, and executable compatibility evidence are distinct: reading an implementation is not evidence of passing its tests.

## Validation workflow ownership

`eng/validation-workflows.json` is the authoritative inventory: six pull-request workflows plus a separate Pages deployment workflow. `scripts/verify-workflow-inventory.py` checks the file inventory; it does not certify execution outcomes.

| Workflow | Responsibility |
| --- | --- |
| `ci.yml` | Warning-as-error native solution builds and tests on Linux, Windows and macOS; real MSBuild input and fingerprint contracts; exact-source archive with commit, tree and content identities. |
| `host-integration.yml` | All eight retained real-process LSP suites, trusted fixture restoration, CLI check/inspect/source/assembly output and LSP packaging. |
| `upstream-compatibility.yml` | Independent pinned XamlX baseline and XamlG source-backend comparison. |
| `theme-corpus.yml` | Unmodified Simple and Fluent XAML/code-behind compilation, generated root construction and control-template realization. Also reusable by tagged releases. |
| `playground.yml` | Production WebAssembly publish and browser acceptance. |
| `release.yml` | All package candidates, package inspection and clean installed-tool/application consumers. Actual publication is separately gated. |
| `pages.yml` | Deployment followed by public build-identity and browser acceptance checks. |

The former standalone solution, tooling, workspace, LSP, checkpoint, shipping and worktree workflows were consolidated rather than retained as duplicate builds. Native OS jobs, host-process validation and the two theme jobs still create independent detached worktrees, use separate outputs and verify that source was not modified before cleanup. They are parallel validation jobs, not autonomous coding agents.

## Native compiler and authoring coverage

`XamlG.slnx` includes compiler/generator execution, tooling/designer, workspace/watch, protocol/publication and real-Avalonia tests. Exact revision counts come from TRX artifacts rather than a static total.

Compiler tests emit and execute C#. Syntax differential tests compare incremental and full parses, including malformed edits. Runtime tests exercise detached replacement, state transfer, publication rollback, failed construction and cleanup. Tooling tests cover source transactions, namespace-safe moves, visual edits, resource budgets and typed-expression inspection. Avalonia tests exercise selectors, themes, compiled bindings, commands/indexers, interaction state and realized visual provenance.

The parser reparses eligible subtrees and retains unaffected positioned nodes; it is not a complete position-independent red/green tree. Work counters do not establish whole-pipeline latency or allocation superiority over XamlX.

## Resources, options and runtime construction

Resource tests compile and execute relative, root-relative and referenced-assembly includes, style includes, eager merges, local/theme precedence, fresh instances and session ownership. Separate assemblies deliberately share a logical filename to check factory identity isolation. Tests also verify unresolved/dynamic sources, duplicate identities, incorrect include types, cycles, dependency failures and recovery.

Ordinary imports and flattened merges must preserve dependency order. Theme-variant providers are initialized before evaluating their contents; dynamic theme keys must execute exactly once and supply both the provider key and insertion key. Option-markup tests require lazy branch evaluation and one receiver construction. Transform-literal tests compare operation structure as well as matrices so that interpolation semantics are not lost by reducing operations to a matrix transform.

Incrementality assertions inspect actual work counts: unchanged documents reuse binding/output, value-only edits can compile one document, export-signature changes invalidate dependents, and dependency diagnostics do not poison cached bindings. Cancellation, explicit cache clearing and statement-emitting operations inside expression-only delegates are covered.

Workspace tests coalesce identical duplicate MSBuild inputs and reject conflicting buffers. The process suites verify coherent unsaved C# and resource overlays without changing the caller's version to manufacture freshness. Other cases cover generic constraints, code-behind factories, initialization idempotence, constructor/cleanup failures, XML entities and UTF-16 mappings, nested markup, template scopes, Roslyn field usage, formatting, refactoring, resource links, and semantic-token history.

## Pinned upstream and theme gates

The original XamlX baseline executes 222 pinned cases. The XamlG adapter executes 217 runtime/diagnostic cases through XamlG binding, generated C#, Roslyn emission and runtime execution. CI checks exact executed counts and rejects skips. Four XamlX AST-shape assertions and one IL-helper-name assertion are explicitly outside the source-backend comparison. See [the harness boundary](upstream-validation.md).

The theme gate independently verifies 81 physical Simple documents plus its declared linked resource, and 86 physical Fluent documents. It retains exact input source/hashes, logical paths, complete diagnostics and generated output. It compiles the original code-behind alongside the XAML without referencing precompiled theme assemblies, then constructs generated theme roots and realizes seventeen controls in Light and Dark variants. Compile-only success does not satisfy this gate. Failed documents must not be excluded to make the gate pass.

XamlX is test-only, not a production dependency. Neither suite certifies every framework extension, version, binary-loader ABI, custom markup extension or visual designer operation.

## Host, package and browser gates

`host-integration.yml` runs `test-lsp-host.py`, `test-lsp-features.py`, `test-lsp-csharp.py`, `test-lsp-diagnostic-contract.py`, `test-lsp-watch.py`, `test-lsp-resources.py`, `test-lsp-pull.py`, and `test-lsp-file-moves.py` against the actual executable. Tests include framed transport, versioned/legacy edits, no implicit writes, references, automatic refresh, diagnostic caching, publication freshness and shutdown.

`eng/release-manifest.json` defines fourteen shipping packages, including the single-reference `XamlG.Avalonia` integration package. Release validation inspects analyzer/runtime dependencies, installs CLI/LSP into a clean tool environment and executes portable and Avalonia application consumers.

`scripts/test-shipping-consumer.py` additionally checks handwritten/URI loader adaptation, repeated initialization, real binary and managed manifest-resource contents, two target frameworks, unchanged-build output timestamps, source edits, resource renames, CLI adapter/source/assembly output, protection of externally edited generated files, and clean/rebuild. Building a resource-bearing assembly without loading its assets is insufficient.

Browser tests run the production compiler and Avalonia preview, inspect source and visuals, test immediate edits, undo/redo, themes, designer gestures, isolated execution/reset and multi-document resources. Pages verifies the exact public build identity and reruns browser acceptance against the deployed URL. Native Razor type-checking is not a WebAssembly publish or browser acceptance result.

PR validation creates candidate artifacts, not a NuGet publication or version-tagged GitHub release. Tagged publication requires package consumers, pinned upstream comparison, the same reusable theme-construction gate and browser acceptance. NuGet additionally requires an explicit dispatch request, version tag and protected environment credentials. A workflow definition or a historical green result does not validate a newer source tree; final readiness must be checked at the exact candidate revision.
