# Feature status and remaining boundaries

This is a capability map, not a declaration of universal XAML/Avalonia parity. A passing corpus proves the cases actually executed, not every possible application or custom extension.

## Added by the authoring and code-behind continuation

| Area | Implementation |
| --- | --- |
| Type binding | Generic base/interface/dependent constraints, constructor and required-member constraints, nested/array substitution, ref-like restrictions, source diagnostics instead of invalid generic emission. |
| Code-behind resources | Eligible concrete/nongeneric `x:Class` factories, actual constructors, service forwarding, one-time/base-and-derived initialization, inherited-session ownership, constructor-failure cleanup, caller-service fallback, public exports, nested metadata names. |
| Linker failure recovery | Backend-only failures suppress dependent output and recover without corrupting reusable caller caches. |
| Host input identity | Shared generator/workspace handling of per-file compile flags, linked logical paths and empty path metadata. |
| Source mapping | Markup value spans, nested markup scanning and decoded-to-raw UTF-16/entity mapping. |
| Name refactoring | Scoped `x:Name`/`x:Reference`, Avalonia static binding name references, generated/explicit code-behind fields and source usages, collision/capture rejection. |
| Formatting/actions | Semantic-aware whitespace edits, full/range formatting, tag rewrites, unique property/event spelling fixes. |
| Navigation | Local resource definitions/links, resource URI completion/hover, loaded C# source references, workspace symbols including loaded closed documents. |
| LSP | Negotiated versioned/legacy workspace edits; full/delta/range semantic tokens; single-flight snapshot analysis; cancellation/freshness checks. |
| Browser integration | Monaco command-palette/context-menu/keybindings for scoped rename, format and actions; rename preview; atomic XAML/C#/resource project undo/redo; stale-plan rejection; nested factory runtime metadata identity. |
| Unsaved C# | Open C# buffers participate in the same XAML semantic snapshot; original parse options are preserved, refactor edits carry current C# versions, and closing restores loaded source without writes. |

## Deliberate runtime and compatibility boundaries

Legacy XamlX-only compiled resource assemblies are not translated automatically. Resource includes require XamlG export metadata. Eager dictionary merge is not IL-level flattening and can allocate transient dictionaries. Generic/abstract code-behind roots, handwritten initialization, explicit root construction directives and unsupported/required-member constructor shapes retain caller-controlled Populate support rather than a generated automatic factory.

Generic nullable-annotation warning parity is not certified. Arbitrary custom markup extensions, framework version changes and the full Avalonia animation/transform corpus require additional compatibility coverage. Existing typed style/animation objects must not be assumed unsupported merely because they are outside the certification corpus.

Structural hot reload builds replacement graphs with eligible state transfer; arbitrary object identity and external side effects are not rolled back. Browser isolation protects the editor origin but is not an operating-system CPU/memory quota.

## Authoring work still outside the implemented surface

Arbitrary C# symbol rename, source-generating refactors, decompiled metadata navigation, XAML file-rename/refactoring edits, semantic-token binding incrementality and pull-diagnostic result caching are not supplied. Name rename does not infer references embedded in runtime string lookups, selector strings or unknown framework-specific conventions. Clients requiring those usages must provide additional semantic reference policies.

Compiler Studio exposes Rename (F2), Format (Shift+Alt+F), source actions (Ctrl+.), and project undo/redo through Monaco actions and toolbar controls. Rename previews all affected XAML and C# buffers; source transactions are atomic and do not execute preview code. Arbitrary C# refactoring and every Visual Studio/Monaco provider surface are not implied. The LSP consumes unsaved C# as XAML compilation input; it does not replace a full C# language server.

## Integration and evidence

PR #5 publishes the recovered 89-file authoring checkpoint and integrates its browser command UI, bounded multi-language transactions, and coordinated C# overlays. All recovered source trees were checked against the published commit before continuing. Compiler/library, LSP, and browser work use isolated worktrees; the normal online CI matrix and installed-package/browser gates must pass before merging.

Run evidence belongs to its exact commit. No NuGet publication or version tag is implied by a source merge. The historical downloadable verification report describes the original offline delivery, not the current repository's publication state.
