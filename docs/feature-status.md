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
| Browser integration | Correct runtime metadata identity for nested code-behind factories. No new browser refactoring UI or project-wide undo is implied. |

## Deliberate runtime and compatibility boundaries

Legacy XamlX-only compiled resource assemblies are not translated automatically. Resource includes require XamlG export metadata. Eager dictionary merge is not IL-level flattening and can allocate transient dictionaries. Generic/abstract code-behind roots, handwritten initialization, explicit root construction directives and unsupported/required-member constructor shapes retain caller-controlled Populate support rather than a generated automatic factory.

Generic nullable-annotation warning parity is not certified. Arbitrary custom markup extensions, framework version changes and the full Avalonia animation/transform corpus require additional compatibility coverage. Existing typed style/animation objects must not be assumed unsupported merely because they are outside the certification corpus.

Structural hot reload builds replacement graphs with eligible state transfer; arbitrary object identity and external side effects are not rolled back. Browser isolation protects the editor origin but is not an operating-system CPU/memory quota.

## Authoring work still outside the implemented surface

Arbitrary C# symbol rename, source-generating refactors, decompiled metadata navigation, XAML file-rename/refactoring edits, coordinated unsaved C# language-service buffers, semantic-token binding incrementality and pull-diagnostic result caching are not supplied. Name rename does not infer references embedded in runtime string lookups, selector strings or unknown framework-specific conventions. Clients requiring those usages must provide additional semantic reference policies.

Compiler Studio does not yet expose the new rename/format/action services through a complete Monaco refactoring UI with atomic XAML+C# multi-document undo/redo. Its existing visual designer and resource editor remain separate from LSP capability support.

## Delivery and evidence

This continuation was implemented from upstream tree `bbb64416e02a0fa917f3d6db21f56ac5adc9ef40`, corresponding to main commit `9273227236612fe3f3914a5327a1a760a558ea8a`. The accompanying delivery verification report records actual local commands, test counts, package validation and environment caveats.

Current GitHub actions exposed to this session permit reads, not commit/branch/PR writes. The local implementation must not be described as pushed, merged, deployed, tagged or released. Prior main's successful CI/deployment belongs to that prior commit and is not substituted for verification of this continuation.
