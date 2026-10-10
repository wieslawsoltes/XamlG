# IntelligentUI: resources, bindings, templates and brushes

These features are part of PR #22's shared IntelligentUI compiler. Studio, desktop sessions, native MCP Avalonia/Wasm guests, portable MCP resources and static export consume the same resolved declarations. Discover the `authoring` contract and `authoringExample` through `xamlg_ui_catalog`, or call `UiAuthoringExamples.Directory()`.

This is the data-only response dialect, not unrestricted application XAML. Its `Binding` syntax is inspired by [Avalonia's public binding syntax](https://docs.avaloniaui.net/docs/data-binding/data-binding-syntax); its supported behavior and boundaries are specified below. The existing trusted XamlG project compiler is a separate execution path. Public host interoperability targets MCP Apps, not a private ChatGPT UI protocol.

## Resource and style authoring

Property elements, `x:Name`, `Classes`, scalar resources, inline merged dictionaries, `StaticResource` and `DynamicResource` references are supported. Resource references resolve in their declaration scope during publication. Local dictionaries override merged entries, and resource cycles, unknown values and duplicate local keys are rejected. No resource include fetches an external URL. `DynamicResource` currently participates in revisioned regeneration, not an independent live CLR resource graph.

Scoped `Styles` lower to real Avalonia styles in the native renderer and validated package-owned CSS in the portable resource. Registered control types, class/name qualifiers, supported pseudoclasses and nested `^` selectors are available. Typed presentation setters use the native catalog's converters; explicit local properties retain precedence. State/action/visibility authority cannot be introduced through a style. Selector combinators, arbitrary includes, control themes and model-authored control templates are not part of this increment.

## Bindings and inherited data contexts

```xml
<StackPanel xmlns="https://github.com/avaloniaui">
  <TextBox Text="{Binding state.name, Mode=TwoWay}"/>
  <StackPanel DataContext="{Binding customer}">
    <TextBlock Text="{Binding name}"/>
    <TextBlock Text="{Binding address.city, FallbackValue='Unknown city'}"/>
  </StackPanel>
  <TextBlock Text="{Binding amount, StringFormat='{}{0:F2} EUR'}"/>
</StackPanel>
```

Unqualified paths begin at the current JSON context: initially `data`, inherited from a declared `DataContext`, or the current template/repetition item. Explicit `state`, `data` and `item` roots remain available. Paths support members, array indices, quoted object keys and empty/`.` paths. Attribute markup and `<Binding Path="..."/>` or `<CompiledBinding Path="..."/>` property objects share one parser.

`Default` and `OneWay` resolve current source values on revisioned regeneration. `Default` on an input bound directly to `state.name` uses the registered native input adapter. Explicit `TwoWay` requires exactly one declared state slot, a registered input property and no formatting/fallback conversion. A text edit therefore uses the same owner, revision, type and range checks as `ui:Bind`. Data objects and nested state paths do not acquire write authority. Do not combine `ui:Bind` with a binding for the same input property.

`FallbackValue` handles a missing path. `TargetNullValue` handles an existing null value. `StringFormat` uses invariant formatting, references only argument zero, and checks format length, alignment, numeric precision and output size before committing. JSON path syntax is bounded to 1,024 characters and 64 segments. A failed binding or oversized result preserves the previous store snapshot.

`CompiledBinding` in this dialect is a validated JSON-path spelling, not a promise of CLR `x:DataType` compiled-binding semantics. `OneTime`, `OneWayToSource`, element/ancestor bindings, arbitrary CLR source objects and converters are not silently emulated. Unsupported options are diagnosed. The separate full project compiler remains the route for trusted application binding code.

## Reusable templates

```xml
<StackPanel xmlns="https://github.com/avaloniaui"
            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
            xmlns:ui="urn:xamlg:intelligent-ui">
  <StackPanel.Resources>
    <DataTemplate x:Key="person">
      <Border Padding="8"><TextBlock Text="{Binding name}"/></Border>
    </DataTemplate>
  </StackPanel.Resources>
  <ContentControl Content="{Binding featured}"
                  ContentTemplate="{StaticResource person}"/>
  <ItemsControl ItemsSource="{Binding people}" ui:ItemKey="{Binding id}"
                ItemTemplate="{StaticResource person}"/>
</StackPanel>
```

`ItemTemplate` supports ItemsControl, ListBox and ComboBox. `ContentTemplate` supports registered content controls. Both accept inline DataTemplate declarations or static resource references. Content may be structured JSON; template content is evaluated against that object while the owning control keeps its own context. Each template has one registered visual root and a deterministic private key prefix. Object rows need stable `ui:ItemKey` values rather than array-position identities.

Templates retain lexical resource scopes. Compatible keyed native controls survive data replacement, and repeated/template actions capture their current item in the owning store. A view cannot forge action context. Unused templates also undergo compiler validation, including unknown properties, forbidden expression syntax and cycles; unreferenced declarations are not an execution loophole.

Templates expand to bounded primitive trees. They are not virtualized Avalonia IDataTemplate instances and do not add control-template parts, templated-parent binding, hierarchical templates or arbitrary object construction. Generated content-context holders count against existing node/depth limits.

## Structured brushes

Native brush properties (`Background`, `Foreground`, `BorderBrush`, `Fill`, `Stroke`) accept catalog color strings, null, or typed brush objects. XAML SolidColorBrush, LinearGradientBrush, RadialGradientBrush, GradientStop and corresponding property elements lower to that same format.

```xml
<Border xmlns="https://github.com/avaloniaui">
  <Border.Background>
    <LinearGradientBrush StartPoint="0%,0%" EndPoint="100%,0%" Opacity="0.7">
      <GradientStop Color="Blue" Offset="0"/>
      <GradientStop Color="Teal" Offset="1"/>
    </LinearGradientBrush>
  </Border.Background>
</Border>
```

```json
{
  "kind": "radial",
  "center": "50%,50%",
  "gradientOrigin": "25%,25%",
  "radiusX": "50%",
  "radiusY": "25%",
  "opacity": 0.8,
  "spreadMethod": "Pad",
  "stops": [
    { "color": "White", "offset": 0 },
    { "color": "Blue", "offset": 1 }
  ]
}
```

Native adapters preserve opacity, 2D matrix transforms/origins, linear endpoints, radial centers/focal points/radii, Pad/Reflect/Repeat spread and ordered stops. Brush properties also work in scoped style setters. Binding the whole brush permits transactional reactive replacement; nested brush-member bindings are not implemented. Static export emits real brush/GradientStop property elements, including null setter values, not a JSON attribute pretending to be Avalonia XAML.

Each brush is limited to 16,384 characters and 64 stops with nondecreasing offsets in `[0,1]`. Radius numeric magnitudes are bounded to `[0.000001, 1000000]`, before percentage normalization, to exclude subnormal division and infinite elliptical transforms. Whole surfaces allow at most 512 distinct gradient descriptors and 1 MiB of their descriptor text. Unknown/duplicate fields, invalid colors, unsupported brush kinds and asset URLs are rejected. These are rendering data, not asset handles or factories.

Portable SVG shapes use prepared, snapshot-owned paint servers; identical brush descriptors deduplicate and obsolete paint servers retire on replacement. CSS backgrounds provide linear/radial projections. Portable text and borders currently use the first gradient stop as their fallback. Mixed units, arbitrary brush transforms, non-square CSS gradient geometry and native stroke/layout details are not pixel-equivalent. Use the native Avalonia guest for exact framework brush behavior. Network/image/font CSP grants are unchanged.

## Validation and remaining framework scope

`UiXamlAuthoringTests`, `UiStyleTreeTests`, `UiBindingAuthoringTests`, `UiBrushAuthoringTests` and `UiAuthoringIntegrationTests` cover authoring, lexical scope, typed values, native style precedence, keyed identity, invalid-update rollback and exported XAML loading. Portable `styles.spec.mjs` and `brushes.spec.mjs` exercise the actual embedded resource; `intelligent-ui-authoring.spec.mjs` exercises the production Avalonia/Wasm guest and companion with the discoverable example.

A test definition is not a passing run. PR #22 records results by exact head and synthetic merge. Headless/native property tests, portable Chromium tests, production browser acceptance, package checks, merging and deployment are separate evidence.

Full authoring parity still requires additional control families, control themes/templates, broader binding/selector semantics, virtualized and hierarchical data views, image/drawing brushes, effects/animation, owner-scoped rich assets/references, advanced charts/tables and independent host/platform conformance. See the [fidelity audit](intelligent-ui-avalonia-fidelity.md) and [rich response guide](intelligent-ui-parity.md). This increment closes concrete resource/style/binding/template/brush gaps; it does not relabel all remaining Avalonia APIs as supported.
