# Validation and upstream provenance

## Pinned inputs

| Input | Revision/version |
| --- | --- |
| Avalonia source inspected for adapter contracts | `17350180c33b063f0e98abbfd19aa3cae63f5d56` |
| XamlX test source | `7ef6aef496ab6e8dcf3df04bef697be49db37c04` |
| Avalonia runtime packages | `12.1.3` |
| Microsoft.CodeAnalysis | `5.0.0` |
| .NET SDK/runtime | `10.0.401` / `10.0.12` |

Source revisions and runtime package releases are recorded independently. Reading an upstream implementation does not count as passing its tests.

## Committed executable suites

| Suite | Coverage |
| --- | --- |
| `XamlG.Tests` | Portable compiler, generated-code execution, source generator, malformed-input handling, dynamic setters, metadata diagnostics and incremental syntax differential tests. |
| `XamlG.Tooling.Tests` | Shared analysis, semantic navigation, designer operations, revisioned history, Unicode-safe minimal buffer diffs and subtree reuse. |
| `XamlG.Workspaces.Tests` | Roslyn project/additional-document snapshots and the explicit MSBuild trust boundary. |
| `XamlG.LanguageServer.Tests` | Framing, malformed headers, truncated payloads, UTF-16 edits, budgets and stale revisions. |
| `XamlG.Avalonia.Tests` | Five focused real-framework tests: namescope/brush construction, bindings, static resources, deferred template instances and realized visual inspection. |
| `XamlG.XamlX.Baseline.Tests` | 222 pinned upstream cases through the original XamlX backend; baseline only, not evidence about XamlG. |
| `XamlG.XamlX.Compatibility.Tests` | 217 upstream cases through XamlG binding, C# emission, Roslyn assembly emission and execution. |

The default solution includes the five local test projects. Upstream projects are opt-in and require the pinned checkout. The historical claim of a 46-case Avalonia compatibility project is not applicable to the committed tree: that suite is not present and is not counted. The focused Avalonia tests do not establish selector, styling or compiled-binding parity.

## Host and package validation

`ci.yml` packs the runtime/source generator, checks analyzer dependency layout and executes a clean package-consuming application. `workspaces.yml` runs standalone CLI source/assembly emission and inspection. `lsp-host.yml` launches the real stdio executable, verifies semantic requests and source recovery, and packs the .NET tool. `solution.yml` builds and tests the default solution on Linux, Windows and macOS. A workflow definition describes the gate; the run's conclusion is the evidence that a particular revision passed it.

`playground.yml` publishes and runs the browser suite. `pages.yml` additionally checks the exact deployed commit and runs browser tests on the public site. Browser coverage includes immediate source edits and undo/redo as well as the initial preview path.

## Upstream comparison boundaries

See [the comparison harness](upstream-validation.md) for the unchanged assertions and explicit exclusions. Exact case counts are enforced by `scripts/verify-test-results.py`; skipped cases are failures. The XamlX assemblies are test-only and cannot become production package dependencies through these projects.

Five baseline cases are outside the source-backend comparison: four XamlX-internal AST representation assertions and one IL-only helper metadata-name assertion. Framework-specific transforms beyond this portable test corpus require their own tests. Full Avalonia styling, compiled binding paths, binary-loader compatibility and structural hot reload are not implied by the portable suite.
