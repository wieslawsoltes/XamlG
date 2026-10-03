# Source-driven design and structural reload

## Runtime identity

Every generated object can carry `XamlSourceInfo`: its source path/revision, exact UTF-16 element span, explicit identity when named, and fingerprints of its declaration and assigned members. `XamlRuntimeSession` supports reverse instance lookup. Realized visuals can map to the nearest generated owner, including framework-created template children; those visuals are not falsely presented as independently source-owned elements.

## Detached replacement transaction

`XamlReloadSession.Reload` checks revision and owning thread, constructs a detached candidate, prepares state-transfer operations, applies those operations only to the candidate, then publishes the new root. A preparation or transfer failure leaves the displayed graph and its subscriptions intact. Publication failure attempts to restore the previous root. A double publication/rollback failure is surfaced as an aggregate error. Cleanup errors after a successful publication are warnings, not a reason to undo a valid replacement.

`XamlNodeMatcher` prefers unique explicit identities, then unique unchanged anonymous declarations. Positional anonymous keys are not trusted after structural edits. Changed source declarations take precedence over preserved interaction state. Adapters can capture application-specific state through `IXamlStateTransferAdapter` without coupling the compiler to framework controls.

The Avalonia adapter transfers eligible text/caret/selection, checked values, range values, expansion, selected index, scrolling, focus and locally supplied DataContext. It does not blindly copy arbitrary fields or replace explicitly changed declarations. Old generated event and binding subscriptions are retired after successful publication.

This is state-preserving structural replacement, not arbitrary object-identity-preserving reconciliation or .NET metadata-delta hot reload. Factories and property setters remain application code: their external side effects cannot be universally rolled back. Ambiguous anonymous matches are intentionally not transferred.

## Visual manipulation

`AvaloniaDesignerSurface` provides actual visual hit testing, source selection, drag movement, eight resize handles, keyboard nudging, grid snapping, Shift aspect locking, Alt free movement and Escape cancellation. Gestures draw a candidate overlay. They do not mutate live controls before source validation.

The default layout policy emits Canvas coordinates for Canvas children and margin changes for other layouts; resize emits dimensions. This is a predictable editable policy, not a universal constraint solver. Custom frameworks or layout engines can replace `IAvaloniaDesignerLayoutPolicy`.

A completed gesture becomes one `XamlVisualEdit`. `XamlBatchDesignerEdits` checks source revision and coalesces same-position attribute insertions into one undo transaction. The playground validates the candidate syntax and semantics before changing document history, then recompiles and reloads.

Insertion/removal/reparenting are source-first operations. Moving a subtree preserves inherited namespaces and `xml:space`; a destination markup-compatibility policy that cannot be preserved is rejected. Cycle-producing moves and stale nodes are rejected. Structure editing is also available from the property inspector; it is not a promise of multi-selection drag-reparenting across arbitrary layout engines.
