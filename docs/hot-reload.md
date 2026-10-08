# Source-driven design and structural reload

## Runtime identity

Every generated object can carry `XamlSourceInfo`: its source path/revision, exact UTF-16 element span, explicit identity when named, and fingerprints of its declaration and assigned members. `XamlRuntimeSession` supports reverse instance lookup. Realized visuals can map to the nearest generated owner, including framework-created template children; those visuals are not falsely presented as independently source-owned elements.

## Detached replacement transaction

`XamlReloadSession.Reload` checks revision and owning thread, constructs a detached candidate, prepares state-transfer operations, applies those operations only to the candidate, then publishes the new root. A preparation or transfer failure leaves the displayed graph and its subscriptions intact. Publication failure attempts to restore the previous root. A double publication/rollback failure is surfaced as an aggregate error. Cleanup errors after a successful publication are warnings, not a reason to undo a valid replacement.

`XamlNodeMatcher` prefers unique explicit identities, then unique unchanged anonymous declarations. Positional anonymous keys are not trusted after structural edits. Changed source declarations take precedence over preserved interaction state. Adapters can capture application-specific state through `IXamlStateTransferAdapter` without coupling the compiler to framework controls.

The Avalonia adapter transfers eligible text/caret/selection, checked values, range values, expansion, selected index, scrolling, focus and locally supplied DataContext. It does not blindly copy arbitrary fields or replace explicitly changed declarations. Old generated event and binding subscriptions are retired after successful publication.

This is state-preserving structural replacement, not arbitrary object-identity-preserving reconciliation or .NET metadata-delta hot reload. Factories and property setters remain application code: their external side effects cannot be universally rolled back. Ambiguous anonymous matches are intentionally not transferred.

## Visual manipulation

`AvaloniaDesignerSurface` provides actual visual hit testing, path-aware source selection, group selection, drag movement, eight resize handles, keyboard nudging, grid snapping, Shift aspect locking, Alt free movement and Escape cancellation. Ctrl/Command/Shift click toggles membership; a plain drag of a selected control moves the group. Group resizing transforms each member's bounds and respects its size constraints. Gestures draw a candidate overlay. They do not mutate live controls before source validation.

The default layout policy emits Canvas coordinates for Canvas children and margin changes for other layouts; resize emits dimensions. This is a predictable editable policy, not a universal constraint solver. Custom frameworks or layout engines can replace `IAvaloniaDesignerLayoutPolicy`.

A completed gesture produces `XamlVisualEdit` entries. Hosts subscribe to `EditsRequested` to commit a group atomically; the original `EditRequested` event remains available for a single selection. `XamlBatchDesignerEdits.FromVisualEdits` checks each path, revision and exact source span, coalesces property insertions, and groups non-conflicting edits by document. The playground validates the full candidate project before publishing one workspace undo entry, then recompiles and reloads. Main and resource XAML use the same transaction path. Geometry rejects missing or shared source instances rather than applying several conflicting edits to a template declaration.

The framework-independent `XamlDesignGeometry` planner supports alignment to an explicit anchor, equal widths/heights/sizes, equal-gap distribution and group transforms. The Avalonia inspector converts preview-root DIP rectangles to source properties through the active layout policy. Arrangement requires siblings. Selection is bounded to 256 controls, and geometry rejects ancestor/descendant combinations, unrealized or hidden targets, stale previews and violated size constraints. Distribution holds the first and last item on its axis fixed; negative gaps represent overlap.

**Inspectors → Designer** exposes selection, grid, mode, gesture cancellation, geometry and arrangement previews. Applying a preview changes XAML in one undo step; **Reload trusted preview** then executes it. Native drag/resize/property gestures retain the app's automatic validated reload. MCP exposes `designer_state`, `designer_configure`, `designer_select`, `designer_hit_test`, `designer_targets`, and separate `designer_geometry_plan/apply` and `designer_arrange_plan/apply` tools. `runtime_run` performs the explicit reload. Source, runtime and designer revisions have separate meanings and checks; a preview also records the project revision it was built from. `xamlg://designer` publishes state-change notifications. Owner UI controls work with agent sharing disabled.

Insertion/removal/reparenting are source-first operations. Moving a subtree preserves inherited namespaces and `xml:space`; a destination markup-compatibility policy that cannot be preserved is rejected. Cycle-producing moves and stale nodes are rejected. Structure editing is also available from the property inspector; it is not a promise of multi-selection drag-reparenting across arbitrary layout engines.
