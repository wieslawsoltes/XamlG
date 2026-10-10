# IntelligentUI selectors and input authoring

This increment extends the repository's revisioned IntelligentUI dialect. Native desktop and
Avalonia/Wasm use the pinned Avalonia 12.1.3 controls; the portable MCP resource projects the
registered operations to DOM/CSS/SVG. It does not implement a private ChatGPT protocol.

## Selector contracts

A selector ends in one registered type, so every setter has a statically validated target.
Source comma groups lower to separate typed rules. The supported grammar includes logical
child (`>`), descendant (whitespace) and native template (`/template/`) relationships; exact
types; `:is(Type)` for registered derived controls; names, classes, supported pseudoclasses;
compound `:not(...)`; and `:nth-child`/`:nth-last-child` with `An+B`, `odd` and `even`.
Nested `^` rules retain their declaring style/theme scope.

```xml
<StackPanel.Styles>
  <Style Selector=":is(Button)"><Setter Property="FontSize" Value="15"/></Style>
  <Style Selector="StackPanel.summary > TextBlock:nth-child(2)">
    <Setter Property="Foreground" Value="Blue"/>
  </Style>
  <Style Selector="Button /template/ Border#chrome">
    <Setter Property="BorderBrush" Value="Teal"/>
  </Style>
</StackPanel.Styles>
```

Parsing is bounded to 512 characters, 16 steps, 64 predicates and 16 source groups. Positional
coefficients are integers in [-4096,4096]. Whitespace does not join split numbers or keywords:
`-n + 3` is valid, while `1 2`, `o d d` and `n + 1 2` are rejected. Static XAML serializes the
validated selector AST into canonical native syntax, including the framework universal
predicate and normalized positional formulas. Reactive exports preserve the original source.

Native selectors are composed from Avalonia Selector objects, not reflective property lookup.
The portable adapter indexes the authored logical tree and counts authored siblings rather
than incidental DOM wrappers. Dynamic hover/focus predicates remain live without publication.
A native template relationship never targets ordinary response content in HTML. Work, path
and emitted-CSS budgets independently bound selector projection.

## Input properties and lifecycle

All registered controls support typed `IsTabStop`, `TabIndex`,
`KeyboardNavigation.TabNavigation`, `AutomationProperties.AutomationId` and
`AutomationProperties.HelpText`. TabIndex accepts 0–32767; omission clears the local native
value rather than guessing a framework default. Native Avalonia puts unspecified indices
after explicit indices, including zero. Portable ordering follows that rule within the response.

Navigation modes are Continue, Cycle, Contained, Once, None and Local. The portable adapter
uses authored groups, skips hidden/disabled/non-tab-stop inputs, remembers focus in Once
groups, and leaves the response for the host document's ordinary controls when appropriate.
Native controls/template focus scopes remain authoritative; this is not arbitrary cross-host
or operating-system focus management.

CheckBox, ToggleButton, ToggleSwitch and RadioButton preserve nullable Boolean values through
source, state, transport and native input events. `IsThreeState` supports mixed values.
Native RadioButton activation remains select-only; it does not acquire checkbox cycling.
Portable three-state checkbox activation sends exactly one typed change and exposes mixed
state accessibly. A focused mixed value is not converted to false when another action commits
the current draft.

TextBox `CaretIndex`, `SelectionStart` and `SelectionEnd` are bounded to 0–16384. Text is
applied before native indices are clamped. Unchanged selection declarations are not reapplied
on unrelated state echoes. Removing or changing a declaration deliberately restores/replaces
that local setting. Portable backward selections and `AcceptsTab` preserve editing behavior;
IME composition and modified keyboard gestures do not trigger ordinary Enter/Tab shortcuts.

Slider supports `SmallChange`, `LargeChange` and `IsDirectionReversed`, along with its existing
range and snapping contract. Arrows, PageUp/PageDown and Home/End use the typed values, with
range clamping and optional tick snapping. Home and End are absolute endpoints.

Button-family `IsDefault` and `IsCancel` are explicit behavior properties, never presentation
style setters. Portable action dispatch requires a declared action and commits the focused
bound draft first. Invalid edits cannot run an action against a previous valid value. Existing
form validation/submission, multiline editing and selection widgets retain their own key
handling. External actions still go through the ordinary review/permission path.

RepeatButton Delay is 0–60000 ms and Interval is 16–60000 ms. The native adapter uses the real
control. Portable held repetition is restricted to declared local state actions. It waits for
each host result before scheduling another tick, stops on errors, release, cancellation, blur,
hidden documents, source replacement or teardown, and cannot build an unbounded request queue.
External-effect actions retain ordinary click/review behavior and never auto-repeat.

TabControl header buttons are reconciled by the authored TabItem key. Selection echoes and
reordering retain compatible headers and their focus. Headers expose selected state and panel
relationships; arrow/Home/End navigation skips disabled/hidden headers.

## Templates and presenters

PR #27 introduced bounded native ControlTemplate, ControlTheme/BasedOn, private template
NameScopes and typed TemplateBinding. TwoWay template binding is restricted to the registered
owner/part input pair. The selector extensions in this PR style those real native parts.
Model-authored callbacks, arbitrary construction and action/state directives in template parts
remain rejected.

The native catalog has 49 entries, including ContentPresenter and ItemsPresenter. Portable
standalone ContentPresenter projects one declared content value and retains keyed content.
An unowned ItemsPresenter is empty, as there is no templated native ItemsControl generating its
containers. The portable adapter does not construct native control templates or claim native
virtualization. All 49 declared types now participate in portable catalog coverage.

## Discovery and executable acceptance

`xamlg_ui_catalog.authoring` exposes selector budgets, navigation modes and input contracts
without removing any previous discovery fields. `UiInputExamples.Controls()` (also returned
as `inputExample`) combines null/two-way inputs, input metadata, local actions, typed selectors,
a control theme and template bindings. No provider key is needed to publish the example.

Native tests verify the actual typed adapters, local-value resets, styling, template parts,
nullable event round-trips and static XAML loaded by Avalonia. Portable tests execute the
assembled shipping resource through a strict revision-checking parent fixture. Production
`intelligent-ui-input.spec.mjs` renders the example in the real Avalonia/Wasm guest and exercises
native checkbox activation, Tab traversal and slider key handlers. The browser workflow runs
this and the authoring fixture explicitly in its targeted IntelligentUI acceptance step.

The upstream behavioral references are the pinned [RepeatButton implementation](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/Avalonia.Controls/RepeatButton.cs),
[RadioButton implementation](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/Avalonia.Controls/RadioButton.cs) and
[selector grammar](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/Markup/Avalonia.Markup/Markup/Parsers/SelectorGrammar.cs).

## Remaining framework scope

These changes do not complete the entire Avalonia API. Additional control families, live and
external theme/resource graphs, broader CLR bindings/property selectors, native virtualized
and hierarchical data authoring, image/drawing brushes, effects/animation, rich asset/reference
components and advanced analytical table/chart interactions remain separate work. Platform,
font, scale and independent commercial MCP-host conformance also require their own validation.
The [fidelity audit](intelligent-ui-avalonia-fidelity.md) and [authoring guide](intelligent-ui-xaml-authoring.md)
distinguish the implemented dialect from those remaining targets. Exact-head CI, merge and
public package/deployment state are separate evidence.
