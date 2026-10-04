# Pinned Monaco lifetime compatibility

The production editor uses Monaco 0.52.2. The downloaded `a356d173` browser trace
identified an unhandled `Canceled` rejection in `Delayer.cancel` called by
`WordHighlighter.dispose`. The source buffer was retained and export succeeded;
the browser correctly failed on the unhandled rejection. This is separate from the
earlier managed editor/module retirement defects.

The pinned upstream `WordHighlighter` schedules work from cursor-position and focus
callbacks but ignores both promises returned by `runDelayer.trigger`. Disposing the
contribution rejects its outstanding delay. `prepare-assets.mjs` now adds
`.catch(onUnexpectedError)` to those two producer call sites in the copied AMD bundle.
The imported upstream error handler consumes CancellationError and continues reporting
other errors. Highlighting is not disabled and no global error or rejection filter is
installed. A related upstream report is microsoft/monaco-editor#5294.

`build/monaco-lifetime-patch.mjs` validates the entire original bundle's SHA-256 and
each unique call site before rewriting it. It fails publication when the package
changes, rather than guessing at renamed minified symbols. Preparation always starts
from the original npm package and never changes node_modules. Review or remove this
compatibility patch when updating Monaco. The MIT license and existing notices remain
with copied assets.

The `preprepare-assets` tests execute the exact inserted expressions against the
pinned upstream Delayer and error handler. They verify pre-disposal cancellation,
normal execution, and observable non-cancellation failures, plus rejection of unknown
bundle contents. A browser regression explicitly queues and cancels highlighter work;
the existing resource-retirement and export checks still reject every page error.

The same trace set found Escape after a rejected file move reaching the page body:
Preview had been disabled while focused, causing browser focus to leave the dialog.
`DialogFocusState` requests focus when opening and after a busy-to-ready transition,
not during ordinary typing or unrelated renders. Both source-authoring dialogs use it.
The original collision/Escape test is unchanged; an additional test verifies focus,
reopening, successful preview cancellation and unchanged file identity.
