# Validation and provenance

The SDK/package versions are pinned in `global.json` and `Directory.Packages.props`. Upstream source pins live in `tests/Upstream.props`: XamlX `7ef6aef496ab6e8dcf3df04bef697be49db37c04` and Avalonia `17350180c33b063f0e98abbfd19aa3cae63f5d56`. Runtime packages and inspected source revisions are tracked independently. Reading an implementation is not the same as executing its tests.

## Local solution

`XamlG.slnx` includes compiler/generator execution tests, source-editing/tooling tests, workspace/watch tests, protocol/publication tests and focused real-Avalonia tests. The solution builds/tests on Linux, Windows and macOS. Test counts grow with the implementation; use the TRX artifacts for the exact revision rather than an outdated static total.

Compiler tests compile generated C# with Roslyn and execute it. Incremental syntax tests compare against full parses, including malformed edits. Tooling tests cover revisioned edits, namespace-safe moves, batch visual edits and typed expression inspection. Runtime tests cover detached replacement, state transfer, publication rollback and subscription cleanup. Avalonia tests cover styling/selectors, themes, compiled bindings, sources/indexers/commands, interactive state and realized visual provenance.

The source-preserving parser currently reparses eligible subtrees and retains unaffected positioned nodes; it is not claimed to be a complete position-independent red/green tree. Parser counters report reparsed characters/reused nodes, not whole-pipeline time or allocation superiority over XamlX.

## Upstream comparison

The original XamlX baseline runs 222 pinned cases. The XamlG adapter runs 217 of those runtime/diagnostic cases through XamlG binding, generated C#, Roslyn emission and execution. CI requires exact executed counts and no skipped cases. Four internal XamlX AST representation assertions and one IL-helper-name assertion are explicitly outside the source-backend comparison. See [the harness boundary](upstream-validation.md).

XamlX is test-only, not a production dependency. Passing this corpus does not certify all Avalonia extension transforms, all framework versions, every custom markup extension or every UI designer operation.

## Host, browser and packages

The CLI smoke path emits assemblies in standalone and evaluated-project modes. LSP tests exchange real stdio messages and mutate source inputs to test automatic refresh. Package consumers build with the incremental generator and its transitive targets. The release candidate installs the shipped CLI/LSP with an empty cache and exercises a real Avalonia package-consuming application.

The browser runs the production compiler and actual framework view. Tests exercise rendering, code-behind, editor snapshots, source/visual inspection, themes/mobile layout, gestures and isolation. Pages deployment additionally verifies the exact public source identity and reruns the browser suite against the public URL.

Release validation creates 13 package artifacts, inventories dependencies and records checksums/source commit. PR validation does not publish NuGet packages or GitHub releases. A particular workflow's successful conclusion and artifacts are the evidence for its revision; a workflow definition alone is not evidence of success.
