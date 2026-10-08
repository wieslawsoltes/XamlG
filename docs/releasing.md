# Build, package and release

`eng/release-manifest.json` is the explicit inventory of twenty-one shipping packages: eighteen compiler/library/integration packages and the `xamlg`, `xamlg-lsp` and `xamlg-studio` tools. `XamlG.Avalonia` is the single-reference integration package. Tests, the test-only XamlX baseline assembly and the browser application are not NuGet library packages.

## Candidate validation

```sh
python scripts/build-release.py --version 0.1.0-preview.2 --output artifacts/release
python scripts/test-release-packages.py --packages artifacts/release --version 0.1.0-preview.2
```

Use a fresh output directory. The script never silently overwrites previous artifacts. `--no-build` is available after building the complete solution with matching `Version` and `PackageVersion` global properties. The example creates a local candidate; it does not claim that this version exists on a public feed.

The candidate contains NuGet packages, portable-symbol packages for ordinary libraries, the exact Git source archive, inventory, release notes and SHA256SUMS. Nuspecs are checked for version, MIT license and exact repository commit. Analyzer dependencies are bundled without shadowing host Roslyn assemblies. Test-only XamlX dependencies are forbidden. The package readme and third-party notices are retained.

The consumer test uses an empty package cache and source mapping: `XamlG.*` comes from the candidate directory; other dependencies come from NuGet. It installs all three tools, executes real stdio/watch tests, emits assemblies with the installed CLI, evaluates a project with the packaged MSBuild host, and executes portable and Avalonia applications through the packaged generator. `scripts/test-studio-packages.py` also builds and runs a standalone automation/agent/MCP consumer outside the repository. Its temporary tool installation, source and package cache are removed on success or failure.

The single-reference shipping consumer additionally covers handwritten/URI loader calls, initialization idempotence, actual binary/managed-resource contents, two target frameworks, unchanged-build timestamps, edits, resource renames, CLI adapter/source/assembly output, refusal to overwrite externally edited generated files and clean/rebuild. Declaring an embedded asset or producing an assembly without loading its contents is not sufficient.

To consume a locally built candidate, add its absolute directory to the project's NuGet sources alongside NuGet.org and use source mapping as appropriate. Set `XamlGVersion` to that candidate's version and reference `XamlG.Avalonia`; its transitive dependencies supply generator/build/runtime integration. Existing Avalonia/framework dependencies remain ordinary package references. The clean-consumer script is an executable example of isolated feed setup.

Pull requests run the candidate path with a CI version. They do not publish GitHub releases or NuGet packages. Readme/license changes are package changes and must also pass candidate validation. A successful exact-revision workflow run is validation evidence; defining a workflow or citing an earlier revision is not.

## Versioned GitHub release

After merging and validating `main`, explicitly create a version tag such as `v0.1.0-preview.2`. `release.yml` verifies that the tag commit is an ancestor of `origin/main`, rebuilds/tests/packages the solution, executes both pinned upstream suites, calls the reusable **unmodified theme-construction workflow**, runs browser acceptance and publishes checksummed artifacts. Prerelease suffixes produce prereleases.

The same theme workflow is reused rather than copied: original source hashes/document counts, code-behind compilation, root construction and Light/Dark template realization remain required. A source-only compile does not satisfy the release gate.

A tag does not automatically publish to NuGet. NuGet requires an explicit workflow dispatch on an existing `v...` tag with `publish_nuget=true`, successful package/upstream/theme/browser jobs and the protected `nuget` environment's `NUGET_API_KEY`. Checksums are verified before publication. Duplicate package versions are skipped to permit resuming a partially published feed, not overwritten.

GitHub assets are published from the validated candidate. Published releases are immutable under the publication script: reruns verify existing checksums and source commit instead of replacing differing public binaries. A partially uploaded draft can be completed only for the same source revision.

## Guarantees and limits

The .NET build is deterministic and artifacts record the source revision. The pipeline does not claim that ZIP timestamps or every third-party build step are byte-for-byte reproducible. The package inventory records dependencies; it is not a vulnerability scan, signing certificate, provenance attestation or full SBOM. Signing and feed credentials are deployment-owner configuration, not embedded in source.

Passing the supported compiler, host, package and pinned framework gates does not certify every custom XAML extension, framework version or legacy binary-loader ABI. The feature and validation documentation records those boundaries separately from release readiness.
