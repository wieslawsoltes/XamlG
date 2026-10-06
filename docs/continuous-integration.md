# Continuous integration and merge evidence

The maintained workflow inventory is `eng/validation-workflows.json`. Six workflows validate pull requests; one deploys the merged product. `scripts/verify-workflow-inventory.py` rejects missing or unlisted workflow files. It validates the inventory, not whether a build is correct or green.

| Workflow | Responsibility |
| --- | --- |
| `ci.yml` | Full native solution builds and tests on Linux, Windows and macOS, plus MSBuild normalization/fingerprint contracts on all three systems. Each matrix job uses its own detached Git worktree. An independent job uploads `compiler-source`, including the exact commit, tree, per-file Git identities and archive checksum even when compilation fails. |
| `host-integration.yml` | Real CLI/LSP stdio, cancellation, evaluated-project, unsaved C#, resource, authoring, pull-diagnostic, file-refactoring and CLI watch/process suites. Browser-component lifecycle tests and sample CLI inspection are retained. Logs and tested script identities are uploaded. |
| `upstream-compatibility.yml` | Pinned XamlG compatibility cases and the separately labelled original-XamlX baseline. Passing the baseline is not XamlG parity evidence. |
| `theme-corpus.yml` | Unmodified upstream theme inputs, source hashes, exact input counts, compilation/emission, root construction and template realization. No theme failure is excluded or bypassed by workflow consolidation. |
| `playground.yml` | Production WebAssembly publish and the unchanged browser acceptance suite, including trusted/isolated loader execution and editor ownership. |
| `release.yml` | Complete package inventory, clean installed tools and real consuming applications, resource-bearing assembly emission, package metadata/checksums and release-candidate artifacts. Publishing remains restricted to its existing tag/explicit-publish path and protected environment. |
| `pages.yml` | Main-only Pages build/deployment followed by deployed-revision and public browser acceptance verification. |

## Removed workflows

One-off source-import/checkpoint restoration and offline toolchain/package-download workflows are no longer part of product CI. Their files were removed rather than leaving dormant write-to-main automation. Historical commits and Actions artifacts are not deleted.

Standalone compiler/Avalonia/tooling/workspace/LSP native test workflows, the separate three-platform solution workflow and the separate MSBuild/worktree lanes were consolidated into `ci.yml` and `host-integration.yml`. Native tests are executed through the complete solution; process suites still execute their actual hosts. The package-consumer coverage supersedes the smaller duplicate two-package smoke workflow. Source recovery uses the normal `compiler-source` artifact rather than special import scripts.

## Merge policy

Evaluate the latest completed runs for the exact PR head (and its tested merge with the current base). All six PR validation workflows above must succeed, including every native platform and both theme jobs. An absent, queued, cancelled, skipped or failed validation job is not a pass. Do not merge because an obsolete workflow count was reached. Recheck the head/base before merging and use an expected-head constraint.

Workflow cleanup does not alter branch protection or authorize bypassing required checks. Repositories with externally configured required check names must keep those rules aligned with this inventory through their normal administrative process. A successful merge is not a NuGet release; tags and publication are separate actions.

CI artifacts are evidence for their recorded source revision only. Local or earlier-commit successes are not substituted for final-head results.
