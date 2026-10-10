# Intelligent UI: Avalonia fidelity and contract audit

This audit accompanies [PR #22](https://github.com/wieslawsoltes/XamlG/pull/22), dated 2026-10-10. Read it with [the core contract](intelligent-ui.md) and [the rich authoring/form guide](intelligent-ui-parity.md).

## What is the specification?

There are three different compatibility targets, and they must not be collapsed into one claim.

1. **Public host interoperability:** the [MCP Apps 2026-01-26 specification](https://github.com/modelcontextprotocol/ext-apps/blob/main/specification/2026-01-26/apps.mdx), including its resource contract, initialization, parent-mediated requests, notifications, capabilities and lifecycle. This is a public protocol target.
2. **Intelligent response behavior:** interactive controls, local reactive values, structured tool data, visual composition and explicit external actions. [OpenUI's article](https://www.openui.com/blog/how-chatgpt-intelligent-ui-works) is an architectural observation, not an authoritative private ChatGPT wire specification. This repository defines its own versioned XAML/C# authoring and resolved-tree contracts.
3. **Avalonia fidelity:** actual Avalonia controls, property behavior, layout and rendering using the repository's pinned Avalonia **12.1.3** dependencies. The native renderer delegates to Avalonia; the model-authoring catalog decides which features can be requested. The portable HTML guest is a CSS/SVG projection, not another implementation of Avalonia.

The default catalog now contains **47 native controls and 18 source-only composites**. This is not complete coverage of every Avalonia public control, property, template, resource or rendering feature. The remaining-work matrix below makes that distinction explicit.

## Architecture and ownership

A publication goes through compilation, declared-property validation, expression evaluation, composite lowering and resolved-tree validation before the owner-scoped session commits it. State/data/local-action updates regenerate and validate a candidate tree transactionally. Renderers consume resolved operations, not arbitrary CLR type names or model-authored HTML.

`UiCatalog` describes allowed values and capabilities. `UiAvaloniaCatalog` supplies application-authored factories, typed setters and input adapters. `UiTreeValidation` enforces cross-property and structural invariants for both compiled and transport-provided trees. New drawing grammars live in `UiDrawingValues`, with explicit numeric, text and element budgets.

The native renderer remains dispatcher-affine. Stable keys retain compatible native instances. An incompatible type, removed node or replaced session retires the old entry. Preparation and live application are covered by one reentrancy guard. The rollback path may reconstruct the previous native tree; it does not promise to preserve object identity after a failing trusted setter.

Application-authored registration callbacks are trusted code. Arbitrary callback exceptions, side effects or blocking behavior do not become isolated merely because the default catalog is bounded. The approved full-C# worker remains a separate explicit execution feature with the limits documented in the rich-authoring guide.

## Defects repaired

| Defect | Repair | Regression evidence |
| --- | --- | --- |
| The outer StackPanel measured a single response root with unbounded height, preventing viewport-constrained star layout. | A single-root surface forwards available measure constraints and arranges the child in the final viewport. Multiple roots retain vertical flow for compatibility. | `UiAvaloniaFidelityTests`: star rows fill remaining space and respond to resize. |
| Disposing the renderer cleared the surface but retained descendant ownership edges. | Detach all owned descendants before retirement and clear event subscriptions and surface references. | Native nested-child removal and disposal tests. |
| Retiring a custom parent could still leave it owning a child retained in the next tree. | Release affected ownership edges before invoking retirement callbacks. | Keyed native lifetime coverage plus the renderer ownership ordering. |
| Reentrancy was not guarded during detached factory/setter preparation. | Keep the applying guard active through preparation, publication and recovery; reject disposal during an update. | Renderer guard and existing atomic-update tests. |
| Sibling membership checks repeatedly scanned the replacement list. | Use reference-identity hash sets for membership filtering. | Existing retained/reordered child tests. Native collection moves still have their own ordering cost; total reconciliation is not claimed to be linear for arbitrary permutations. |
| New typed tuple setters initially used a stricter grammar than the existing property schema. | Preserve the existing comma/space tuple grammar for thickness, radii and offsets; keep separate strict drawing grammars. | `UiFeatureLifetimeTests.New_typed_setters_accept_the_existing_catalog_tuple_grammar`. |
| The layout-transform render bridge can retain an upstream property subscription. | Clear `UseRenderTransform` when its owning renderer retires the control. | Removal/disposal bridge tests. |
| Portable nested content slots kept alignment styles after the properties were removed. | Restore only the adapter-owned slot style fields before applying the next declaration. | `content-alignment.spec.mjs`, including retained child identity. |
| Host-context patches without a theme reset the current theme. | Apply only supplied valid context fields. | `host-context.spec.mjs`. |
| Portable incoming messages checked the parent window but did not retain the initialized host origin constraint. | Require the initialized origin as well as the parent source. Ignore cyclic/non-JSON or oversized host messages. | Portable origin-pinning/lifecycle tests. |

## Added native authoring capabilities

### Controls and containers

`Path`, `Polyline`, `Polygon`, `LayoutTransformControl` and `Label` have explicit native constructors and schema declarations. Their structural contracts are derived from existing shape, single-child or content registrations; they do not inherit input or action authority accidentally.

`LayoutTransformControl` participates in Avalonia measure/arrange. A 90-degree matrix applied to a 60 by 20 child produces a 20 by 60 desired size in the native regression. `RenderTransform` remains a visual transform rather than a request to allocate transformed layout space. Prefer explicit `LayoutTransform` for deterministic authored layout; `UseRenderTransform` retains Avalonia's upstream bridge behavior rather than defining a new animation system.

### Common visual and layout properties

Every default primitive gains `ZIndex`, `IsHitTestVisible`, `UseLayoutRounding`, `FlowDirection`, `RenderTransform`, `RenderTransformOrigin` and geometry `Clip`. Signed margins are accepted between -10000 and 10000. Declared minimum dimensions may not exceed declared maxima.

Panel backgrounds are exposed consistently across the registered layout panels. Registered templated controls gain typed background, border, padding, corner-radius, foreground and font properties. Registered content controls expose horizontal and vertical content alignment. Removing a property clears its local native value instead of leaving the previous publication's value behind.

The schema intentionally remains finite: grid indices/definitions, dimensions and child counts retain their documented budgets. A supported property name does not imply unrestricted values or the ability to construct arbitrary resource objects.

### Typography and editing

Registered text controls support local `FontFamily`, `FontStyle`, font sizes from 0.1 to 512, `TextTrimming`, `MaxLines` and `LineHeight` where applicable. `TextBox` gains `TextWrapping` and `AcceptsTab`. Portable tab insertion updates the draft and respects the current length limit without moving focus.

Font-family names are not font assets. URI-based font loading, embedded font registration, OpenType feature selection, arbitrary inline runs, rich-document editing and host-provided font-face stylesheet injection are not supplied by this increment. Native shaping/fallback uses Avalonia and the fonts available to the host; no cross-platform pixel identity is implied.

### Scrolling

`ScrollViewer` exposes horizontal and vertical scrollbar visibility, `AllowAutoHide` and `Offset`. Native properties use Avalonia's coercion/layout behavior. Portable offsets are applied through the existing layout scheduler after insertion rather than through an unbounded timer. CSS scrollbar behavior remains subject to the browser and operating system.

Use one constrained root such as `Grid` for full-height content. The single-root fix does not make a child fill the window when the embedding application itself offers an unbounded measure constraint.

### Drawing and clipping

All six registered shapes expose stretch and pen properties: dash array/offset, line caps, joins and miter limit. Native operations use Avalonia `Geometry`, `MatrixTransform`, `RelativePoint`, typed point lists and native pen properties. Static export retains these as Avalonia XAML literals.

The portable renderer creates SVG elements through DOM APIs, never through model-authored HTML. It converts Avalonia pen-relative dash lengths and offsets into SVG user-space lengths. Geometry validation happens before the new drawing values can mutate the displayed tree.

## Literal grammars and budgets

| Value | Accepted grammar and bounds |
| --- | --- |
| `RenderTransform`, `LayoutTransform` | `matrix(m11,m12,m21,m22,offsetX,offsetY)` or `none`. Exactly six finite invariant coefficients, each between -1000000 and 1000000. |
| `RenderTransformOrigin` | Two absolute coordinates or two percentages, for example `0,0` or `50%,50%`. Mixed units are rejected. |
| `Path.Data`, `Clip` | Bounded M/L/H/V/C/S/Q/T/A/Z path data, absolute/relative forms, repeated argument groups and exponent notation; optional Avalonia F0/F1 prefix. A nonempty path starts with a move. Arc radii are nonnegative and flags are 0 or 1. This is a defined bounded grammar, not every shorthand admitted by every SVG parser. |
| Polygon/polyline `Points` | Complete x,y pairs; at most 512 points. |
| `StrokeDashArray` | At most 64 values in 0..10000. A nonempty array must contain a positive length. |
| Drawing text | At most 16384 characters; path data at most 512 segment groups. |
| `FontFamily` | Nonempty local family name, at most 128 characters, without asset URI/markup syntax. |
| `ZIndex` | Integer -32768..32767. |

These checks supplement existing source, expression, node, depth, state and tool-data limits. They do not grant filesystem, network, navigation, clipboard or tool execution authority. Geometry failure, an incomplete point pair or an invalid size range cannot replace a committed store snapshot. Native transport snapshots receive the same semantic drawing validation before setters run.

For a complete example, call `UiDrawingExamples.LayoutAndDrawing()` or inspect `drawingExample` from `xamlg_ui_catalog`. Discovery also returns a `drawing` section describing matrix, origin, geometry, pen, layout, asset and portability contracts. Agents do not have to infer these grammars from a generic `Text` descriptor.

```xml
<Grid xmlns="https://github.com/avaloniaui"
      xmlns:ui="urn:xamlg:intelligent-ui"
      Width="640" Height="320" RowDefinitions="Auto,*">
  <Label Grid.Row="0" Content="Drawing" FontSize="24" Padding="12,8"/>
  <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
    <Canvas Width="800" Height="500">
      <Path ui:Key="curve" Width="240" Height="180"
            Data="M10,90 C40,10 80,10 120,90 S200,170 230,90"
            Stroke="Blue" StrokeThickness="3" StrokeDashArray="4,2"
            StrokeLineCap="Round" Stretch="Uniform"
            RenderTransform="matrix(1,0,0,1,24,24)"
            RenderTransformOrigin="0,0"/>
    </Canvas>
  </ScrollViewer>
</Grid>
```

## Public MCP host-context support

The portable resource handles the public [host context](https://apps.extensions.modelcontextprotocol.io/api/interfaces/app.McpUiHostContext.html) and documented [style-variable keys](https://apps.extensions.modelcontextprotocol.io/api/types/app.McpUiStyleVariableKey.html): theme, bounded color/font/size tokens, locale, timezone metadata, platform, incoming display mode, touch/hover capabilities, safe-area insets, and fixed/maximum container dimensions.

Notifications are patches: omission preserves unrelated current fields. Invalid optional locale/timezone values are ignored. Only known style-variable names and bounded token values are applied. URL/image/executable stylesheet constructs are rejected; arbitrary host stylesheet blocks and remote font-face loading remain excluded by this resource's CSP. Locale/timezone metadata propagation is not a claim that every existing numeric/date formatter has been localized.

Capabilities still control parent-mediated operations. Incoming display-mode metadata does not implement every optional host request, and a browser harness is not certification against every commercial MCP host.

## Native, portable and exported behavior

| Surface | What executes | What the tests establish |
| --- | --- | --- |
| Desktop/embedded native renderer | Actual registered Avalonia controls and properties. | Native property values, finite measure/arrange, control identity, cleanup, input contracts and validation. |
| Avalonia/Wasm guest | The same Avalonia adapter/library in the browser. | Existing guest lifecycle/bridge tests; the separate production browser workflow is required for real published-runtime integration evidence. |
| Portable MCP resource | Trusted CSS/SVG projection with native browser inputs. | Actual embedded-resource execution in Chromium, typed requests, SVG attributes, CSS properties, host context, teardown and update behavior. |
| Static XAML export | Ordinary Avalonia markup containing resolved values. | Runtime XAML loading and matching native geometry/transform/pen/property assertions for the new shape fixtures and drawing example. |

Portable layout transforms compute transformed intrinsic bounds; they do not reproduce Avalonia's full constraint solver. SVG viewport/stretch projection, DockPanel behavior, scrollbar policy, line trimming, font fallback, clipping and control chrome may differ from native output. The tests are not pixel-diff proofs, GPU backend benchmarks, accessibility certification, or browser-matrix certification.

## Remaining work for broader parity

| Area | Current state | Required next acceptance work |
| --- | --- | --- |
| Complete Avalonia control API | 47 registered primitives; trusted custom registrations remain possible. | Enumerate the pinned public control surface, classify application-only controls, and add typed factories, structural/input/action adapters, export and lifecycle tests for missing public widgets. Include menus/popups/flyouts, specialized selectors/editors, splitters, grids and additional panel families rather than counting internal template parts as finished product features. |
| Styles, themes and resources | Native controls use the embedding application's styles; the model schema exposes selected literal properties. | A bounded resource/style graph, theme variants, selector semantics, resource references, setters and deterministic invalidation, without arbitrary CLR/resource loading. |
| Templates and data binding | Keyed repeated declarations and registered scalar/array values; source composites lower to primitives. | Typed item/content/control templates, richer item data, selection identity, resource lifetimes and virtualization. Define safe binding/converter semantics instead of silently accepting unrestricted runtime XAML. |
| Brushes and graphics | Solid catalog colors, six shape controls, bounded geometry, 2D matrices and clipping. | Structured gradient/image/drawing brushes, richer geometry composition, effects, shadows, masks and animation/composition contracts with budgets and native/export tests. |
| Full native input behavior | Existing registered typed inputs, local actions and form lifecycle. | Rich selection models, broader keyboard/accessibility automation, IME/touch/stylus, pointer capture, gestures, drag/drop and context commands with explicit host authority where needed. |
| Rich Intelligent UI content | 18 composites, local state actions, bounded charts/tables, owner-scoped tool data. | First-class server-resolved images, references/entities/products/citations/maps and mixed Markdown/XAML response composition. Rich text/code editing is not supplied by selectable code text. |
| Analytical tables/charts | Bounded single-series geometry and composed table cells. | Multiseries and additional chart families, zoom/brush/tooltips, sorting/filtering/paging, virtualized tables and accessible structured alternatives. |
| Full MCP interoperability | Existing public initialization/tool/resource/lifecycle support plus the host-context improvements above. | Versioned conformance fixtures for optional capabilities, display-mode requests, all supported host policies and actual independent host interoperability runs. No private ChatGPT operation-wire compatibility is asserted. |
| Assets | Default resource remains network-denied; fonts are local family names. | Owner-scoped bounded asset handles, MIME/size/dimension validation, authenticated resolution, caching/retirement and narrowly scoped CSP grants. |
| Production rendering evidence | Native headless tests and Chromium resource tests. | Real desktop/Wasm screenshots on multiple platforms, DPI/font/theme matrices, focus/accessibility checks, graphics-backend tests and performance budgets. |
| Execution isolation | Existing explicit full-C# approval and dedicated browser worker/supervisor limits. | OS/process isolation when the embedding scenario requires it. Wasm linear-memory limits are not whole-browser heap/CPU quotas. |

These are outstanding capabilities, not hidden implementations. Completing a registration count does not complete these contracts. Native Avalonia engine fidelity and portable HTML approximation must continue to be reported separately.

## Validation and source map

The PR records completed checks against exact source revisions. During this increment, checkpoint `b26bf7547e90a78b67d1fb0574608b7be401be90` was validated by [Intelligent UI run 38051625688](https://github.com/wieslawsoltes/XamlG/actions/runs/38051625688), using GitHub's synthetic merge `0877a19693ee6b9f525ffc8b064bd2da4f563f9e` against base `f1536cae668df0ece3329ed883b3fac8bde260ea`: the solution built with zero warnings/errors; all **2928 .NET tests**, including **237 IntelligentUI tests**, and all **47 portable Playwright tests** passed. Later cleanup/example commits require their own checked run; the PR body tracks that final evidence.

Primary implementation files are `UiAvaloniaFeatureSchema.cs`, `UiDrawingValues.cs`, `UiTreeValidation.cs`, `UiAvaloniaFeatureCatalog.cs`, `UiAvaloniaRenderer.cs`, `UiDrawingExamples.cs`, `UiAutomation.Discovery.cs`, `Resources/ui-avalonia-features.js`, `Resources/ui-host-context.js`, and the existing runtime/bootstrap/control modules.

Regression suites include `UiAvaloniaFidelityTests`, `UiDrawingFidelityTests`, `UiFeatureLifetimeTests`, `UiDrawingDiscoveryTests`, `avalonia-fidelity.spec.mjs`, `content-alignment.spec.mjs`, `host-context.spec.mjs` and the updated catalog/discovery tests. Existing local-action, repeated-context, forms, archive, ownership and MCP tests remain in the same validation runs.

```sh
dotnet build XamlG.slnx -c Release -warnaserror
dotnet test XamlG.slnx -c Release --no-build
cd tools/XamlG.Playground
npm ci --ignore-scripts --no-audit --no-fund
npx playwright install --with-deps chromium
npx playwright test --config=playwright.ui.config.mjs
```

A passing checkpoint, a passing final PR revision, a merged PR, published packages and a deployed website are different evidence. This audit does not substitute one for another.
