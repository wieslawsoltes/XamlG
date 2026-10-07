# Feature status and remaining boundaries

This is a capability map, not a declaration of universal XAML/Avalonia parity. A passing corpus proves the cases actually executed, not every possible application or custom extension.

## Compiler and authoring capabilities

| Area | Implementation |
| --- | --- |
| Portable XamlX behavior | Typed/parameterless markup providers, collection replacement followed by additions, runtime collection overload dispatch, delegate/event values, scoped member/constructor conversions and intrinsic property elements. Shared regressions also cover primitive grammar and decimal runtime conversion. See the [implementation audit](upstream-validation.md#implementation-audit-beyond-the-pinned-tests). |
| Avalonia binding type scopes | Annotated data-type properties and directive precedence; compiled `DataContext` result inference; item templates and assigned bindings typed from generic collections; custom ancestor collection metadata. Named and parent sources preserve forward-reference types, template boundaries and metadata-pass ordering. Method-valued delegates, command overloads, null-conditional paths and leading streams have differential coverage. The [complete transform inventory](avalonia-transform-audit.md) maps the pinned compiler surface to implementation and tests. |
| Avalonia registered values | Registered properties dispatch unset values and provided bindings, preserve template-priority alternatives and convert typed string providers before assignment. Differential cases cover read-only wrappers, provider evaluation, assigned-binding metadata, implicit content and qualified base registrations. Style setters retain provided values. |
| Type binding | Generic base/interface/dependent constraints, constructor and required-member constraints, nested/array substitution, ref-like restrictions, source diagnostics instead of invalid generic emission. |
| Code-behind resources | Eligible concrete/nongeneric `x:Class` factories, actual constructors, service forwarding, one-time/base-and-derived initialization, inherited-session ownership, constructor-failure cleanup, caller-service fallback, public exports, nested metadata names. |
| Linker failure recovery | Backend-only failures suppress dependent output and recover without corrupting reusable caller caches. |
| Host input identity | Shared generator/workspace handling of per-file compile flags, linked logical paths and empty path metadata. |
| Avalonia build directives | Root precompilation opt-outs preserve raw resources and original loader paths; class/field visibility follows the pinned build contracts. Tooling and project caches recover when directives change. Package targets enable deferred-resource function pointers for the Avalonia profile. |
| Source mapping | Markup value spans, nested markup scanning and decoded-to-raw UTF-16/entity mapping. |
| Name refactoring | Scoped `x:Name`/`x:Reference`, Avalonia static binding name references, generated/explicit code-behind fields and source usages, collision/capture rejection. |
| Formatting/actions | Semantic-aware whitespace edits, full/range formatting, tag rewrites, unique property/event spelling fixes. |
| Navigation | Local resource definitions/links, resource URI completion/hover, loaded C# source references, workspace symbols including loaded closed documents. |
| LSP | Negotiated versioned/legacy edits; full/delta/range semantic tokens; single-flight snapshot analysis; cancellation/freshness checks. |
| Resource file refactoring | Simultaneous file/folder move planning, resolved incoming/outgoing include rewrites, candidate-project validation, source/identity collision checks, original-URI versioned or legacy edits. The client owns physical moves. |
| Pull diagnostics | Document/workspace full and unchanged reports; loaded closed files and bounded related resources; removed-file clears; actual wire-value equality; bounded per-URI LRU retention; coalesced negotiated refresh and legacy push fallback. |
| Diagnostic streaming | Request-token opt-in for document and workspace pulls, primary-before-related ordering, bounded awaited batches, unchanged IDs/removal clears and per-frame snapshot freshness; no duplicated terminal values. |
| Browser integration | Monaco scoped rename/format/actions, multi-file previews, atomic XAML/C#/resource project undo/redo, stale-plan rejection, resource move preview/apply/cancel with typed dialog state. |
| Unsaved C# | C# open buffers participate in the XAML semantic snapshot; original parse options are preserved, refactor edits carry current C# versions, and closing restores loaded source without writes. |

## Diagnostic and publication contracts

Previous diagnostic result IDs are bounded opaque strings. Empty, foreign, unknown or evicted IDs produce full reports, not an invalid-parameters error. URIs and provider identifiers still require valid nonempty values. Cached equality includes range, severity, code, source, message and ordering. Reports use immutable values; cache hits do not skip current project analysis.

The cache validates required input fields and computes retention cost before mutation. Result-ID overflow occurs before eviction or replacement. Oversized reports remain complete, do not acquire tokens and retire only their own older entry. Character accounting is a retention estimate, not an exact managed heap byte count.

Cancelling a shared-analysis consumer does not cancel its siblings. Publication rechecks the project and complete open-buffer snapshot under the serialized output gate. A dependency edit can supersede a queued caller report even if that caller's client version did not change. Once admitted, a frame completes or its connection fails; cancellation does not permit a partial frame to be followed by another message.

Streaming retains the existing coherent semantic analysis and bounded related-document traversal; it does not make binding incremental or stream individual diagnostics from one document. See [partial-result protocol and limits](diagnostic-streaming.md).

## Deliberate runtime and compatibility boundaries

Legacy XamlX-only compiled resource assemblies are not translated automatically. Resource includes require XamlG export metadata. Eager dictionary merge is not IL-level flattening and can allocate transient dictionaries. Generic/abstract code-behind roots, handwritten initialization, explicit root construction directives and unsupported/required-member constructor shapes retain caller-controlled Populate support rather than an automatic factory.

Generic nullable-annotation warning parity is not certified. Arbitrary custom markup extensions, framework version changes and the full Avalonia animation/transform corpus require additional compatibility coverage. Existing typed style/animation objects must not be assumed unsupported merely because they are outside the certification corpus.

Structural hot reload builds replacement graphs with eligible state transfer; arbitrary object identity and external side effects are not rolled back. Browser isolation protects the editor origin but is not an operating-system CPU/memory quota.

## Authoring work still outside the implemented surface

Arbitrary C# symbol rename, source-generating refactors, decompiled metadata navigation and semantic-token binding incrementality are not supplied. Name rename does not infer references embedded in runtime string lookups, selector strings or unknown framework conventions. File moves rewrite recognized static compiled-resource include sites, not arbitrary project declarations or runtime resource strings. Linked physical paths need explicit logical mapping; unsupported CDATA/multi-fragment rewrites are rejected.

Compiler Studio's source transactions are atomic and do not execute preview code. Arbitrary C# refactoring and every Visual Studio/Monaco provider surface are not implied. The LSP consumes unsaved C# as XAML compilation input; it does not replace a full C# language server.

## Integration and evidence

PR #5 integrated semantic authoring, browser commands, bounded multi-language transactions and C# overlays. PR #6 adds resource-file refactoring and pull diagnostics, including typed file-move dialog state and final cache/protocol/package regression coverage. See [the PR6 validation contract](pull-diagnostic-validation.md).

Shipping loader/MSBuild integration and diagnostic partial results include real stdio and installed-package coverage. Each change must be validated at its own final head; earlier PR evidence is not a substitute for running new regression cases. The [compiler validation checkpoint](upstream-validation.md#theme-checkpoint) records the completed pinned transform audit and its executed tests.

Run evidence belongs to its exact commit. No NuGet publication or version tag is implied by a source merge. Historical downloadable reports describe their original offline delivery, not the current repository's publication state.
