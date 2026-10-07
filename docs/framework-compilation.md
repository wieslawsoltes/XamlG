# Avalonia compilation

The Avalonia adapter uses Roslyn metadata symbols and public runtime contracts. It does not execute framework code while binding, load user assemblies for reflection lookup, call XamlX as a fallback, or modify application IL.

## Selectors and typed style values

The source-located selector parser lowers type selectors, `:is`, universal selectors, classes/pseudo-classes, names, child/descendant/template combinators, nesting (`^`), `:not`, registered-property comparisons, nth-child/nth-last-child and selector lists into public `Selectors` API calls. Invalid syntax produces compiler diagnostics.

Style, ControlTheme and ControlTemplate scopes carry the target type, including derived styles and custom templates whose class, base class or interface declares `ControlTemplateScope`. Control themes require an explicit target; target types support text and `x:Type` object forms. Empty or absent selectors infer their owning control through an optional `Styles` collection, while selectorless styles inside a control theme are rejected. `x:SetterTargetType` takes precedence over the inferred style target. Setters require a known style scope even for qualified properties; a control template alone does not supply that scope. A nearer style with an unknown target stops lookup, while template scopes are skipped. Explicit setter targets also supply the type for compiled self bindings.

`Setter.Property` requires literal text and follows the pinned property-identifier grammar. Unqualified names use the CLR member's value type and declaring registration, with the pinned fallback to attached accessors declared on the target type. Qualified owners use the `AvaloniaProperty<T>` field's type argument and need no CLR wrapper. Literal setter values ignore member-specific converters. Object and provided values retain their existing types, and an omitted Value remains null. Property assignment is emitted before Value regardless of XML attribute order.

Unprefixed `Classes.name` property references use the public class-property factory and Boolean conversion. Class setters support literal values and live bindings; the nearest style metadata rejects selectors ending in an activator, while selectors ending in a type or `:is` remain valid. The `Classes` owner is case-insensitive. Class lists become collection operations. These transformations remain in the framework profile.

`ContainerQuery.Query` supports the pinned framework's width/height features, `min-`/`max-` comparisons, `and`, and comma-separated alternatives. Attribute and property-element forms lower to typed `StyleQueries` calls. The parser reports located diagnostics without loading Avalonia. Tests execute the same input through the test-only Avalonia 12.1.3 runtime compiler and XamlG, including named containers reacting to layout changes. Compatibility retains two upstream quirks: bare `height` uses the maximum-height comparison, and conjunction grouping follows the compiled transform's behavior rather than the different reflection parser.

Control templates validate inherited `TemplatePart` metadata against their deferred namescope. Required missing parts and incompatible part types are errors; optional missing parts are informational. A derived declaration overrides the same part from a base control. Names in an outer graph or nested data template do not satisfy the containing control template's requirements, and wrong-type diagnostics point to the part's name.

Style warnings follow the pinned compiler's scope and severity. Repeated literal setter names in direct style/theme content produce warnings; aliases and explicit `Setters` property elements retain upstream's different warning behavior. Compilation preserves both setters, so Avalonia can still reject them when applying the style. Item-container warnings check the known owner's `ItemTemplate` or `DataTemplates`, direct template content and the upstream `ContentControl` restriction. Merged-dictionary warnings retain the upstream exclusion for nodes wrapped in target-type metadata.

Explicit styled and attached property assignments inside control-template scopes use `BindingPriority.Template`, allowing style triggers to override their values. Typed static setter metadata preserves member identity, source declarations, initialization timing and live setters. Direct properties and implicit content assignments retain the pinned compiler's CLR-setter behavior. Bindings keep their own priorities; properties marked with `Avalonia.Data.AssignBindingAttribute` store binding objects as values.

```xml
<StackPanel xmlns="https://github.com/avaloniaui">
  <StackPanel.Styles>
    <Style Selector="Button.primary:not(:disabled)">
      <Setter Property="Width" Value="144" />
      <Setter Property="Background" Value="#336699" />
    </Style>
  </StackPanel.Styles>
  <Button Classes="primary" Content="Compiled style" />
</StackPanel>
```

## Compiled bindings

`CompiledBinding` always requests static binding. Ordinary `Binding` follows the scoped `x:CompileBindings` value and host default. Generator and workspace hosts share `AvaloniaBuildOptions`: `XamlGCompileBindingsByDefault` takes precedence over `AvaloniaUseCompiledBindingsByDefault`; the profile defaults to true when neither is set. `x:CompileBindings="False"` is an explicit reflection-binding opt-out, not a fallback for failed static checking.

`x:DataType`, an explicit binding DataType or a statically typed source supplies the path type. Object-valued DataContext assignments can establish an inferred type; an untyped template does not silently inherit its owner's view-model type.

```xml
<TextBox xmlns="https://github.com/avaloniaui"
         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
         xmlns:vm="clr-namespace:MyApplication"
         x:DataType="vm:EditorModel"
         Text="{CompiledBinding Name, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />
```

Markup-extension and binding object-element forms normalize to the same input. Path/member metadata, relative sources and command-target typing survive normalization. Property/indexer/field accessors and stores are typed IR nodes and emitted delegates, not raw expression-string substitution. The bound-tree inspector exposes delegate parameters and bodies.

Implemented path operations include property chains, generated writable accessors, constant indexers, arrays, attached properties, casts, named/self/parent sources, task/observable streams and method-to-command binding. `ElementName` supports forward names. RelativeSource handles DataContext, Self, TemplatedParent and typed FindAncestor with logical/visual tree selection. Source arguments are mutually exclusive and cannot silently combine with source-qualified paths. Explicit two-way modes reject unwritable paths.

A direct binding adapter returns its subscription, and generated code registers it with the runtime session. Disposing a retired graph releases those subscriptions as well as generated event handlers. Framework-owned style/template subscriptions retain their framework lifecycle.

## Type constraints and authoring references

The portable binder validates generic type-argument constraints before emitting C#: dependent base/interface constraints, `class`/`struct`/`unmanaged`, public parameterless `new()` construction, required-member constructor contracts and ref-like argument restrictions. Numeric and user-defined conversions do not satisfy a generic constraint. Constraint substitution supports nested constructed types and arrays. Nullable-annotation warning parity is not claimed.

The Avalonia profile exposes name-reference policies to reusable tooling: `ElementName` and `#name` binding paths, including markup/object/property-element forms, resolve within the compiler's namescopes. Renames preserve raw XML text outside the precise reference spans, including encoded Unicode/entity values. Unsupported string-based lookup conventions are not guessed.

`x:Name` uses the ordinary `Name` property at its position in the attribute list, including markup providers and static values. Statically string-valued assignments declared by an `INamed` implementation register their original values after the setter succeeds. Runtime names may contain spaces, punctuation or other values that are not C# identifiers. Literal names supply compiled-binding source metadata; providers do not. Multiple literal aliases register independently, repeated registration of the same object succeeds, and duplicate names on different objects retain Avalonia's runtime validation. Generated code-behind fields include each valid C# alias while retaining the original runtime namescope keys.

## Build integration

`AvaloniaFrameworkProfile.Create(createSourceInfo: true)` enables Avalonia's public `XamlSourceInfo` metadata on constructed object elements and markup extensions. Locations use one-based XML line/column positions and the physical syntax document path. Metadata is attached after construction and before initialization; populating an existing root preserves its metadata. Resource keys receive locations immediately, including entries whose values are deferred. Compiled resource merges preserve imported locations and record local overrides. This metadata is separate from XamlG's session-owned editing metadata and remains available after session disposal.

The generator and workspace hosts read `XamlGCreateSourceInfo`, then `AvaloniaXamlCreateSourceInfo`, and otherwise enable metadata for `Configuration=Debug`. Direct profile creation defaults to disabled. `XamlWorkspaceOptions.CreateSourceInfo` overrides project configuration.

Color-valued brush literals construct fresh immutable brushes. URI, row/column definition and recognized cursor literals use resolved constructors and receive source locations; runtime parser results do not receive construction metadata. Converter instances receive metadata before evaluating their input and calling `ConvertFrom`. Explicit string objects retain runtime conversion, so `<SolidColorBrush><x:String>Red</x:String></SolidColorBrush>` uses the mutable brush parser while direct text uses the immutable brush intrinsic. Whitespace-only scalar and collection attributes are ignored unless the property accepts strings/objects or its collection retains whitespace. TimeSpan shorthand, grid lengths and vector-like structures are validated through framework-independent parsing; color grammar is checked without loading framework assemblies and color arithmetic uses the application's public parser.

Structured constants preserve the pinned parser's optional-token behavior and affine matrix lowering. A runtime string-object matrix conversion still retains perspective components. Static text trimming, decoration, transparency and theme values follow their individual casing and whitespace rules. Lists support arrays, `IList<T>`, `IReadOnlyList<T>` and `AvaloniaList<T>` derivatives, including inherited `AvaloniaListAttribute` separators/split flags and point-coordinate pairs. Collection construction, capacity and item evaluation follow the pinned order. Literal collections and their embedded constructors do not acquire source metadata; keyed resource locations remain available before realization.

Enum literals use exact field names; only flags enums split comma-separated names and trim their whitespace. Numeric text accepts signed 64-bit input and preserves the enum's underlying bits, including overflow wrapping. Member converters override this intrinsic conversion, while enum-level converters can handle unsupported text. Registered properties, style setters, property selectors and boxed resources share these portable conversion rules.

`IXamlTypeConverterProvider` supplies converter symbols before declared type-converter attributes. Avalonia uses this hook for its compiler-supplied image, bitmap, image-brush source, icon, culture, URI, point-list, TimeSpan, font-family and generic Avalonia-list mappings. Member converters, literal intrinsics and public `Parse` methods retain their precedence. Runtime strings and unsupported intrinsic list conversions can therefore use the framework's actual converters, including their asset-service context, tokenization and runtime exceptions. Malformed primitive list items retain binding diagnostics. Lookup uses Roslyn symbols; conversion executes in the generated application.

Strings already assignable to scalar string/object/interface properties bypass member converters. Collection properties still give an explicit item conversion priority over an assignable whole collection value. Explicit string elements follow content whitespace normalization, including `xml:space` and collection whitespace metadata, while preserving their runtime conversion path. XamlG retains its existing empty string-element support, which extends the pinned compiler.

Use the `XamlG.Generator` package with `XamlG.Runtime`, plus `XamlG.AvaloniaRuntime` for Avalonia binding/runtime integration. The transitive build targets supply XAML AdditionalFiles, expose generator configuration, and disable competing Avalonia XAML/name-generation paths when XamlG is enabled. Existing handwritten `AvaloniaXamlLoader.Load` initialization must be migrated to the generated initializer; a source generator cannot rewrite an existing method body.

`tests/AvaloniaPackagingSmoke` is a real package-consuming project, not a direct-library compilation test. It validates the adapter through MSBuild and the generator after NuGet packing.

## Compatibility boundary

The pinned 217-case XamlG suite validates the upstream portable runtime/diagnostic assertions included by its harness; the separate 222-case original-XamlX run is a baseline. Framework extension contracts, advanced animation/resource-include transforms, framework-version changes and every possible custom markup extension are not certified by that count. Unknown or unsupported paths must diagnose rather than silently fall back. This remains a development compiler, not a claim of unconditional drop-in compatibility for every existing Avalonia application.
