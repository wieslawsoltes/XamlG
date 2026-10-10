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
