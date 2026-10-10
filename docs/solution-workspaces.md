# Solution workspaces

The Studio project system separates portable file/solution editing from trusted SDK execution. A solution is not a flattened collection of source files, and reading project XML is not MSBuild evaluation.

## Architecture

- `XamlG.ProjectSystem`: browser-compatible, revisioned virtual files; structural `.sln`, `.slnx`, `.slnf` and project inspection; solution/project creation; deterministic offline starter templates; conflict-checked file and project edits.
- `XamlG.Workspaces`: the native SDK boundary. Real `MSBuildWorkspace` evaluation preserves Roslyn project identities, references, parse options and diagnostics. SDK processes use argument lists, cancellation and bounded output.
- Studio: solution explorer, project/solution wizard, source and project-file documents, import/export and explicit local SDK operations.

## Trust and capabilities

Opening or inspecting files in browser storage does not authorize execution. Native evaluation, restore, builds, tests and template installation can execute project/package/template code and require explicit trust. The local companion is disabled for filesystem work unless `XAMLG_STUDIO_WORKSPACE_ROOT` is set. Requests use the existing paired-browser owner authentication and session cancellation, not the MCP client token. Paths accepted for filesystem operations are confined to that root and symlink/reparse traversal is rejected. This path policy is not a sandbox for trusted MSBuild tasks or templates.

A standalone browser cannot launch the installed .NET SDK. Offline project inspection must identify itself as structural, preserve unsupported conditions/imports, and never advertise an unevaluated graph as an evaluated build. The paired companion supplies genuine SDK evaluation/build/template operations. Existing browser XAML preview remains a separate capability; it must not be described as building every target of an arbitrary solution.

## Acceptance checks

Cover path escapes and portable filename collisions; atomic stale-revision edits; XML entity rejection; solution formats, folders and project references; lossless retention of unknown project elements; template collisions and generated file validity; native trust rejection, process cancellation and SDK graph loading; browser project creation/import/edit/export and local owner-authentication boundaries.

Implementation and exact-head validation results are tracked in the workspace pull request. This document is an architecture/acceptance contract, not a declaration of complete Visual Studio feature parity.

## Editing and recovery guarantees

Browser changes are prepared against an immutable workspace fork, written with an
IndexedDB revision check, and only then published to the explorer. This applies to
file transactions, open-entry changes, and startup-project selection. Quota errors
or another tab's newer snapshot leave the active selection and saved source intact.
Opening a document persists its tab path; unsaved text is not silently written by
opening another tab. Storage connections recover after failed or blocked opens,
and abandoned connections are closed rather than blocking later database upgrades.

Project-reference matching normalizes relative slash variants, so adding a Windows
`..\Library\Library.csproj` reference from the browser does not duplicate the same
`../Library/Library.csproj` reference. Package versions authored as child metadata
are updated in that form, with conditional metadata preserved. Remove-reference
commands remove only unconditional authored `Include` items; they do not edit
imports, conditional references, central package versions, or `Update` items.
Structural XML edits retain the original declaration instead of changing UTF-8
projects to an incorrect UTF-16 declaration.

In a paired local workspace, the explorer's build target chooses the current
solution/open entry, selected loaded project, or startup project. The main Build
solution command continues to target the open entry. SDK execution still requires
explicit trust. Refreshing disk captures active editor text first, drops stale
inventory entries, and retains open buffers for files deleted externally without
silently recreating or overwriting those files.

## Workspace acceptance checks

The `Solution workspaces` workflow runs the native workspace tests, production
browser publish, and `solution-workspace.spec.mjs` independently of the other
Studio feature suites. The browser scenarios cover `.sln` and `.slnx` creation,
adding a class-library project, editing/removing project and package references,
Unicode source save/reload, storage-failure rollback, competing revision-checked
IndexedDB writes, and an owner-paired native SDK creation/build/Roslyn evaluation.
The companion uses a fresh temporary workspace root, never a developer's checkout.
The regular browser suite also includes these scenarios. Startup and database
connection-recovery contracts run with the existing Node asset tests.

These checks do not turn structural browser inspection into MSBuild evaluation.
Arbitrary SDKs, imports, targets, analyzers, templates and native builds execute
through the explicitly trusted companion. The evaluated Roslyn graph is a snapshot,
not a claim of a persistent Visual Studio project-system or debugger replacement.

Workspace document tabs participate in the docking close guard. Cancel keeps the
buffer, Save checks its before-image, and Discard leaves saved source untouched.
Tab removal is persisted before releasing the buffer; a storage conflict leaves
the tab and dirty text available. Retiring or replacing a workspace also removes
its obsolete dock panes. Acceptance covers close cancellation, save/discard,
reload without closed tabs, and quota failure during close.

## Cancelling trusted SDK operations

The Explorer, workspace output pane and project wizard expose **Cancel operation**
while restore/build/rebuild/clean/test, Roslyn evaluation or installed-template
operations are waiting for the companion. Cancellation aborts only that HTTP
request. It does not revoke the paired owner session, disconnect the coding-agent
stream, cancel a sibling request, or discard editor buffers. Session revocation
still cancels all requests linked to that owner. The existing companion request
cancellation token flows through the serialized SDK service to the process runner.

Cancellation is not rollback: a build, restore, package installation or template
may already have changed files. The UI makes that boundary explicit and does not
automatically repeat an interrupted operation or treat cancelled creation as a
success. Local file saves are completed before starting the cancellable SDK
request. Browser tests exercise the real Cancel control with a held HTTP request,
then perform actual companion restore/build/evaluation on the same connection.
Native tests verify running/queued cancellation and release of the SDK operation
lease. Neither fixture represents an OS-level process-termination measurement.
