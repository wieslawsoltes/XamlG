# Build, package and release

`eng/release-manifest.json` is the explicit shipping inventory: eleven reusable libraries and the `xamlg` / `xamlg-lsp` tools. Tests, the test-only XamlX baseline assembly, and the browser application are not NuGet library packages.

## Candidate validation

```sh
python scripts/build-release.py --version 0.1.0-preview.2 --output artifacts/release
python scripts/test-release-packages.py --packages artifacts/release --version 0.1.0-preview.2
```

Use a fresh output directory. The script never silently overwrites previous release artifacts. `--no-build` is available after building the complete solution with matching `Version` and `PackageVersion` global properties.

The candidate contains NuGet packages, portable-symbol packages for the ordinary libraries, the exact Git source archive, package inventory, release notes and SHA256SUMS. Every nuspec is checked for the requested version, MIT license and exact repository commit. Analyzer dependencies are bundled without shadowing the host Roslyn assemblies. The inventory must not contain test-only XamlX dependencies. Package readme and third-party notices are retained.

The clean-consumer test uses an empty package cache and source mapping: `XamlG.*` comes from the candidate feed, other dependencies come from NuGet. It installs both tools, executes real stdio/watch tests, emits assemblies with the installed CLI, evaluates a project with its packaged MSBuild host, and builds portable and Avalonia applications through the packaged generator. The Avalonia consumer exercises typed style setters, names, compiled and two-way binding, and subscription retirement.

Pull requests run this release-candidate path with a CI version. They do not publish GitHub releases or NuGet packages. A workflow run is the evidence of validation; defining a workflow is not itself a passing result.

## Versioned GitHub release

After merging and validating `main`, push a version tag such as `v0.1.0-preview.2`. `release.yml` verifies that the tag commit is an ancestor of `origin/main`, rebuilds/tests/packages the shipping solution, runs both pinned upstream suites, runs the browser suite, and publishes the checksummed artifacts as a GitHub release. Versions containing a prerelease suffix are marked as prereleases.

A tag does not automatically publish to NuGet. NuGet publication requires explicitly dispatching the workflow on an existing `v...` tag with `publish_nuget=true`. The `nuget` deployment environment must supply `NUGET_API_KEY`; its protections are retained. The job verifies checksums before publishing. Duplicate package versions are skipped to permit resuming a partially published feed, not overwritten.

GitHub release assets are published from the validated candidate. Published releases are immutable under the publication script: a rerun verifies the existing checksums and source commit, rather than replacing differing public binaries. A partially uploaded draft can be completed only for the same source revision.

## Guarantees and limits

The .NET build is deterministic and the artifacts record the source revision. The pipeline does not claim that ZIP timestamps or every third-party build step are byte-for-byte reproducible. The package inventory records declared dependencies; it is not a vulnerability scan, signing certificate, provenance attestation or full SBOM. Signing and feed credentials are deployment-owner configuration, not embedded in source.
