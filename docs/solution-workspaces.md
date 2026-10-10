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
