# IntelligentUI Avalonia fidelity and contract audit

PR #22 extends the shared IntelligentUI compiler, native Avalonia renderer and portable MCP resource. With the follow-up native-template work, the catalog contains **49 native controls and 18 source-only response composites**. Component count is not a claim of full Avalonia authoring parity.

The detailed [resource, style, binding, template and brush authoring guide](intelligent-ui-xaml-authoring.md) specifies the new source grammar, reactive behavior, validation and portability boundaries. `xamlg_ui_catalog` exposes these contracts and the executable `drawingExample` and `authoringExample`.

## Distinct compatibility targets

The repository's `xamlg.intelligent-ui/1` dialect is a versioned response-authoring contract with bounded data and explicit authority. [MCP Apps](https://modelcontextprotocol.io/extensions/apps/overview) is the public host-interoperability target. Avalonia **12.1.3**, pinned in `Directory.Packages.props`, supplies the actual native controls/layout/rendering APIs. These are three separate targets.

The architectural inspiration described by OpenUI is not an official specification of a private ChatGPT DIL or operation wire format. No such compatibility is claimed. Likewise, a portable CSS/SVG projection is not a replacement Avalonia layout engine. Full trusted application XAML uses the existing XamlG project compiler and explicit execution workflow, not arbitrary CLR construction through a response catalog.

See [selectors and input authoring](intelligent-ui-selectors-input.md) for PR #30, its executable examples and precise native/portable boundaries. The current catalog has 49 controls, including the presenters added with native control-template support.

## Native renderer repairs

A single response root now receives the host's finite measure constraints and final arrange rectangle. The old unconditional StackPanel introduced infinite height, breaking viewport-constrained star rows and scrolling. Multiple roots retain vertical flow. Tests check Grid sizing and resize behavior.

Retirement detaches owned descendants and changed parent/child edges before invoking callbacks. Compatible keyed controls survive reparenting and structural updates. The renderer's reentrancy guard covers preparation, detached setters, live mutation and recovery; disposal during an update is rejected. LayoutTransformControl's opt-in render bridge is cleared on retirement to release its property subscription.

Sibling membership checks use reference-identity hash sets rather than repeated scans. Native collection moves retain the framework collection's own ordering cost. Successful updates preserve compatible control identities; recovery after an arbitrary trusted setter failure may reconstruct the previous snapshot and does not promise to undo external application side effects.

## Native property and drawing surface

New primitive controls are Path, Polyline, Polygon, LayoutTransformControl and Label. Shared additions include matrix transforms/origins, geometry Clip, ZIndex, hit-test visibility, layout rounding, FlowDirection, signed margins, Name and Classes. Panel backgrounds, templated-control typography/borders/padding, content alignment, text trimming/line limits, TextBox wrapping/tab input and ScrollViewer policies/offset are cataloged explicitly.

Shapes support stretch, pen-relative dash arrays/offsets, caps, joins and miter limits. The drawing reader validates path syntax, matrix coefficients, point pairs and arc flags before publication or native conversion. Paths allow at most 512 segments, point lists 512 points, dash arrays 64 values, and drawing text 16,384 characters. Coordinates/matrix coefficients remain bounded; invalid size relationships and all-zero dash patterns are rejected.

Brush properties accept solid, linear and radial brush declarations, including opacity, matrix/origin, relative coordinates, gradient spread and ordered stops. Native setters construct actual Avalonia brushes without reflective factory lookup. Structured brush properties and style values export to real brush/GradientStop XAML elements. Brush text, stop counts and distinct whole-surface gradients have independent budgets. Image and drawing brushes are not enabled implicitly.

## Authoring and behavior matrix

| Area | Implemented | Remaining boundary |
| --- | --- | --- |
| Property elements | Registered scalar values, control content/children, Grid row/column definitions, brush and binding objects | Arbitrary CLR object graphs, collection owners and directives are not automatically accepted |
| Resources | Inline dictionaries/merges, lexical lookup, static/dynamic source references, duplicate and cycle detection | No remote includes or general live/theme-variant resource graph |
| Styles | Native typed presentation setters, logical child/descendant/template relationships, :is/:not/positional predicates, source groups and nested `^` rules; local precedence | No arbitrary includes, property selectors or behavior-changing setters |
| Bindings | JSON paths/indices/quoted keys, inherited DataContext, explicit state/data/item scopes, bounded fallback/null replacement and invariant formatting | No CLR source objects, converters, ancestor/element references or arbitrary binding modes; CompiledBinding is a JSON-path spelling here |
| Input updates | TwoWay/Default registered inputs, nullable toggles, native keyboard/range/caret/timing metadata, with ordinary owner/revision/type/range checks | Nested data mutation and arbitrary CLR property writeback are not implied |
| Templates | Reusable inline/resource ItemTemplate and ContentTemplate, lexical resources, instance-scoped keys, current-item action capture, unused-template validation | Data expansion is bounded and nonvirtualized. Native ControlTemplate/ControlTheme/BasedOn and typed TemplateBinding are implemented; broader hierarchical/virtualized data templates remain |
| Native visuals | All catalog entries have explicit matching factories/setters; geometry/transforms, layout/content properties, typography and three brush kinds are available | Full Avalonia public API, third-party controls, effects and animations require more registration and semantic work |
| Response behavior | Existing local/state actions, keyed rows, retained tool data, forms and revisioned streaming remain intact | Broader chart/table interactions and first-class rich asset/reference components remain separate work |
| Export | Static resolved native XAML, styles, brush elements/null values and reactive C# wrapper | Static export is a resolved view, not lossless recovery of source templates/bindings or runtime interaction history |

These authoring features do not grant tool execution, filesystem/network access or reflection. Source/transport validation, owner isolation, revision checks and normal external-action review remain in effect. Unused templates and resources are not an escape hatch for forbidden code or namespaces.

## Portable rendering and host contract

Portable controls use trusted DOM/CSS/SVG, not model-supplied markup or script. Keyed SVG elements survive valid updates; malformed drawing/brush declarations are checked before DOM publication. Avalonia pen-relative dashes are converted to SVG user-space lengths. Removed local visual properties and nested content alignment are reset. TextBox.AcceptsTab changes the local draft instead of moving focus.

Scoped style descriptors generate package-owned CSS after validating type, property, selector and resource budgets. Explicit local properties retain precedence over generated scoped/pseudoclass rules. Snapshot-owned SVG paint definitions deduplicate identical gradient descriptors and retire on replacement or teardown. CSS backgrounds project gradients; portable text/borders use the first gradient stop as their fallback. Non-square CSS gradient geometry, mixed coordinate units, brush transforms and native template/layout details need the native guest for framework fidelity.

The MCP resource applies partial host theme/style-token changes, locale/timezone metadata, safe-area insets and fixed/maximum container dimensions without resetting unrelated context. The initialized parent origin remains pinned. Cyclic/non-JSON or oversized host messages are ignored. The network-denied CSP is retained; stylesheet/font-face injection and remote asset loading are not added. Host tokens are not permission grants.

## Validation and provenance

Native coverage includes finite root/star sizing, ownership/reparenting/disposal, trusted factory/setter recovery, exact schema/factory inventories, geometry/brush values, property resets, style precedence, JSON binding scopes, keyed template reuse, contextual actions and static XAML loaded by Avalonia. The runtime XAML loader is test-only, not a production response-rendering fallback.

Portable Playwright coverage executes the assembled embedded resource: catalog controls, scoped styles, geometry/pen properties, brush replacement/deduplication/rejection, state/form workflows, parent-origin checks, context patches, hostile text and teardown. `intelligent-ui-authoring.spec.mjs` adds production Avalonia/Wasm and companion acceptance for the discoverable authoring example. Existing IntelligentUI suites cover streaming, local actions, forms, persistence, full-C# approval and lifecycle.

Historical checkpoint `609216650e4e1bb7269c6d107c1eff386399541c`, tested merge `0d13875031bbb18011f4b788094863f51152d205`, passed 2,965 .NET tests (242 IntelligentUI) and 48 portable tests. That checkpoint does **not** certify later authoring/brush revisions. PR #22 records newer exact-head results, tested base/merge identities and links to retained source/TRX/browser artifacts. Main may advance during validation; a base branch name alone is not provenance.

```sh
dotnet build XamlG.slnx -c Release -warnaserror
dotnet test XamlG.slnx -c Release --no-build
cd tools/XamlG.Playground
npm ci --ignore-scripts --no-audit --no-fund
npx playwright install chromium
npx playwright test --config=playwright.ui.config.mjs
# After production browser publication and Release companion build:
python ../../scripts/test-browser-studio.py tests/intelligent-ui-authoring.spec.mjs
```

Test definitions, completed native/portable/browser runs, package validation, merge, NuGet publication and Pages deployment remain distinct evidence. No full parity or deployment claim follows merely from adding a catalog entry.

## Remaining work for broader parity

Additional framework control families, template/theme/resource graphs, broader binding and selector semantics, virtualized/hierarchical item views, image/drawing brushes, effects/animation, broader input/accessibility behavior, asset-resolution policies, advanced analytical charts/tables and first-class rich references are still not completely covered by this dialect. Independent commercial-host interoperability and platform/font/scaling comparisons remain necessary. The [rich response guide](intelligent-ui-parity.md) tracks response-specific gaps; the [authoring guide](intelligent-ui-xaml-authoring.md) defines the newly implemented subset precisely.
