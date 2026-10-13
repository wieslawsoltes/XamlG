# Build, package and release

`eng/release-manifest.json` is the inventory of **24 shipping packages**: 21 compiler, runtime, generator, integration and host libraries, plus the `xamlg`, `xamlg-lsp` and `xamlg-studio` .NET tools. It includes `XamlG.ProjectSystem`, which `XamlG.Workspaces` depends on, and both intelligent UI libraries. The [README package table](../README.md#nuget-packages) links to every package.

Every production project under `src/` and `tools/` must appear in the manifest or explicitly declare `IsPackable=false`. Samples, tests, the test-only XamlX assembly, benchmarks, performance/trace utilities and the browser app are not NuGet packages. The shipping projects must also be present in `XamlG.slnx`. Manifest and package checks reject missing internal dependencies, mismatched versions and missing package metadata before publication.

## Candidate validation

Use the .NET SDK pinned in `global.json`, Python 3.10 or later, and an empty output directory:

```sh
dotnet build XamlG.slnx -c Release -warnaserror
dotnet test XamlG.slnx -c Release --no-build --no-restore
python3 -m unittest discover -s scripts/tests -p 'test_release*.py' -v
python3 scripts/build-release.py --version 0.1.0-preview.2 --output artifacts/release
python3 scripts/test-release-packages.py --packages artifacts/release --version 0.1.0-preview.2
```

The version is an example, not a claim that it exists on a public feed. All packages in a release use the same three-part version, optionally with a prerelease suffix. The tag is `v` followed by that version. Numeric version/prerelease components cannot have leading zeroes; build metadata is not used because NuGet normalizes it out of package identity.

Commit the source before building a publishable candidate. For local testing of uncommitted edits, add `--allow-dirty` to `build-release.py`. This marks the inventory `sourceDirty: true`; both publication paths refuse that candidate. Its source archive records the base commit, not the uncommitted edits. The script never silently overwrites artifacts. `--no-build` is available only after building the complete solution with matching `Version`, `PackageVersion` and `ContinuousIntegrationBuild=true` properties.

A clean candidate contains 24 `.nupkg` files, 20 `.snupkg` files for ordinary libraries, the exact Git source archive, package inventory, release notes and `SHA256SUMS`. The generator bundles its analyzer dependencies and the tools bundle their execution dependencies, so these four packages do not have separate symbol packages. The SDK provides Source Link metadata in portable PDBs. All packages include the README, MIT license and third-party notices, a specific description, repository/project URLs and the source commit. Compiler-host Roslyn assemblies and test-only XamlX dependencies must not leak into the analyzer package.

The consumer suite uses an empty package cache and source mapping: `XamlG.*` resolves only from the candidate directory; other dependencies come from NuGet.org. It restores and loads every standalone library, executes project-system APIs, installs all three tools, runs real stdio/watch tests, emits assemblies through the installed CLI, evaluates projects through the packaged MSBuild host, and executes portable and Avalonia applications through the generator. Standalone agent/MCP and intelligent UI consumers run outside the repository too. Temporary installations and caches are removed after the checks.

The single-reference `XamlG.Avalonia` consumer covers handwritten/URI loader calls, initialization idempotence, binary/managed-resource contents, multiple target frameworks, unchanged-build timestamps, edits, resource renames, CLI adapter/source/assembly output, refusal to overwrite edited generated files, and clean/rebuild. To consume a candidate yourself, add its absolute directory to the project's NuGet sources alongside NuGet.org and use that candidate's version.

## One-time GitHub configuration

The repository already contains the release workflow and generated changelog configuration:

- `.github/workflows/release.yml`: candidate validation, tagged GitHub releases and explicit NuGet publication.
- `.github/release.yml`: categories for GitHub's generated release notes.

In [repository Settings → Environments](https://github.com/wieslawsoltes/XamlG/settings/environments), create an environment named **`nuget`**. Under deployment branches and tags, choose **Selected branches and tags** and allow tags matching **`v*`**. Do not add a branch rule for publication. Add required reviewers if you want a human release approval, and configure bypass/self-review options to match your release policy.

GitHub release creation uses the automatically supplied `GITHUB_TOKEN` with `contents: write` in that job only. No personal access token or GitHub Packages credential is needed. If your repository or organization restricts Actions, allow the actions referenced by the workflow, including `NuGet/login`. Only the NuGet job receives `id-token: write`; build and pull-request jobs cannot request the publishing identity.

## NuGet trusted publishing credentials

There is **no long-lived `NUGET_API_KEY` secret to create or rotate**. The workflow exchanges a GitHub OIDC token for a short-lived NuGet key immediately before pushing. Set it up as follows:

1. Sign in to [NuGet.org](https://www.nuget.org/) with the account that will publish the packages. Confirm the account's email and package ownership. For organization-owned packages, use an active member account and select that organization as the policy owner.
2. Open **your username → Trusted Publishing → Add a new policy**. Configure these exact values:

   | Field | Value |
   | --- | --- |
   | Policy owner | Your NuGet account or the organization that owns the packages |
   | Repository owner | `wieslawsoltes` |
   | Repository | `XamlG` |
   | Workflow file | `release.yml` — filename only, without `.github/workflows/` |
   | Environment | `nuget` |
   | Package scope/glob | `XamlG.*` |
   | Allowed operations | Publish new packages and publish new versions |

3. Save the policy. Ensure its ownership and scopes cover all 24 package IDs, including the three tools. Publishing a new ID still requires that the ID is available or owned by the selected account/organization.
4. In GitHub's **`nuget` environment → Environment secrets**, add **`NUGET_USER`** containing the publishing user's **NuGet profile name**, not their email address, GitHub login by assumption, organization name, password or API key. For example, the profile URL `https://www.nuget.org/profiles/example-user` means the value is `example-user`. You can also set this username with the CLI:

   ```sh
   gh secret set NUGET_USER --env nuget --repo wieslawsoltes/XamlG
   ```

   Enter only that NuGet profile name at the prompt. No other NuGet secret is required.
5. If a policy shows **pending activation**, publish within its activation window or restart the window in NuGet's UI before your first publication. NuGet documents a seven-day window for policies awaiting repository identity verification.

The implementation follows [NuGet's trusted publishing setup](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) and the official [NuGet/login action](https://github.com/NuGet/login). The action is pinned to the commit for v1.2.0. NuGet's temporary API key is valid for one hour, so login happens after downloading and verifying the release. The workflow passes the masked output through an environment variable and does not print it.

An old `NUGET_API_KEY` secret is not used by this workflow. Remove it after confirming trusted publishing works. Local candidate builds and tests require no publishing credentials.

## Create the GitHub release

Merge the prepared changes into `main`, wait for the required checks, and choose an unused version. For example:

```sh
git switch main
git pull --ff-only
git tag -a v0.1.0-preview.2 -m "XamlG 0.1.0-preview.2"
git push origin v0.1.0-preview.2
```

The **Release packages** workflow verifies that the tag's commit is an ancestor of `origin/main`, builds/tests/packages the solution with warnings treated as errors and NuGet auditing enabled, runs clean consumers, executes both pinned upstream suites, reuses the unmodified theme-construction workflow, and runs browser acceptance. The theme gate retains original source hashes/document counts, code-behind compilation, root construction and Light/Dark template realization.

Only after all gates pass does it create a GitHub draft, upload checksummed assets and publish the release. Prerelease versions are marked as prereleases. Release notes contain the source identity and GitHub-generated changes using `.github/release.yml`. A partially uploaded draft can be resumed for the same source commit. Published assets are never overwritten; reruns compare checksums and source identity and fail if rebuilt bytes differ.

Pushing a tag **does not yet publish to NuGet**. Pull requests and ordinary manual candidate runs only produce Actions artifacts. The GitHub release is the durable, validated artifact set used for NuGet publication.

## Publish that release to NuGet

After the tagged GitHub release succeeds, run:

```sh
gh workflow run release.yml --repo wieslawsoltes/XamlG \
  --ref v0.1.0-preview.2 -f publish_nuget=true
```

Alternatively, open **Actions → Release packages → Run workflow**, select the existing `v...` tag and enable `publish_nuget`. Selecting a branch with publishing enabled fails explicitly. Approve the `nuget` environment deployment if you configured reviewers.

The publication job downloads the already published GitHub release; it does not rebuild packages or repeat validation against different bytes. It rejects drafts, mismatched tag versions/source commits, local dirty candidates, missing or extra packages/symbols, unexpected dependencies and checksum mismatches before requesting an OIDC token. It then pushes all `.nupkg` files and explicitly pushes all `.snupkg` files, including when package versions already exist. `--skip-duplicate` allows retrying a partial publication; existing versions are never replaced.

Check the workflow's NuGet job and all package pages linked from the README after NuGet finishes validation/indexing. If publication fails partway through, dispatch the **same tag** again; it reuses the same GitHub assets. Fixes to package contents require a new version/tag. A missing GitHub release must be resolved by completing its tagged validation/release run first.

Common setup errors:

| Failure | Check |
| --- | --- |
| `NUGET_USER` is missing | Secret exists in the environment named exactly `nuget`. |
| OIDC exchange/policy mismatch | NuGet username, policy owner/membership, repository owner/name, workflow filename and environment all match. |
| Environment deployment blocked | A tag rule permits `v*`, and any required review is approved. |
| New package/version forbidden | Policy scopes allow both operations and cover `XamlG.*`; the policy owner can publish each ID. |
| Candidate/source/checksum mismatch | Use the original published assets for the exact tag; create a new version for changed binaries. |

## Guarantees and limits

The .NET build is deterministic and artifacts record the source revision. ZIP timestamps and every third-party build step are not claimed to be byte-for-byte reproducible, which is why NuGet publication reuses GitHub's validated assets. The dependency inventory is not a full SBOM or vulnerability scan; shipping build/pack runs retain NuGet's dependency auditing. Source Link and symbols aid debugging but do not constitute a signing certificate or provenance attestation.

Passing the supported compiler, host, package and pinned framework gates does not certify every custom XAML extension, framework version or legacy binary-loader ABI. A workflow definition or an earlier green run does not validate a newer source tree. The [feature](feature-status.md) and [validation](validation.md) documents record those boundaries separately from packaging readiness.
