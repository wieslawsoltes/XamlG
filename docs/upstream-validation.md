# Upstream validation

The revision pins are in `tests/Upstream.props`. Production packages have **no dependency on XamlX**.

## Two distinct suites

`XamlG.XamlX.Baseline.Tests` runs the pinned upstream SRE/IL compiler and all 222 test cases available in that build configuration. The only harness adjustment is locating the runtime interfaces inside the combined test-reference assembly. This is the baseline, not a test of XamlG.

`XamlG.XamlX.Compatibility.Tests` routes compilation through XamlG syntax, Roslyn binding, typed C# emission and Roslyn assembly emission. It runs 217 upstream runtime/diagnostic cases: 198 linked test cases and 19 dynamic-setter cases. Test bodies and assertions are retained. A test-only source generator replaces the IL-specific dynamic-setter configuration with the public XamlG setter-provider extension point. Exception categories and legacy diagnostic IDs are translated at the harness boundary.

Five baseline cases are not counted as XamlG compatibility tests: four `ParserTests` cases assert XamlX's internal AST representation; `Dynamic_Setter_For_Public_Property_Should_Be_Shared` asserts an IL-only helper metadata name. C# output has different internal types and helper identifiers. These exclusions are explicit, not silently skipped successes.

The suites do not cover every custom transformer used by every XAML framework. In particular, passing the portable XamlX tests does not establish full Avalonia styling, selector or compiled-binding compatibility.

## Implementation audit beyond the pinned tests

Compare behavior against the pinned source as well as its tests. Native compiler regressions cover implementation details. The two upstream harnesses also link 85 additional cases from `tests/XamlG.XamlX.ParityCases` to execute identical assertions through both compilers. These authored differential tests use the `ParityRegression` category and separate TRX files; they do not increase the count of unmodified upstream assertions.

| Upstream behavior | XamlG implementation and evidence |
| --- | --- |
| `ProvideValue` and `ProvideTypedValue` alternatives | `RoslynTypeSystem.MarkupExtensionMethod` shares provider selection across binding and value-type probing. Parameterless providers take precedence; typed returns win within the same parameter shape. `MarkupExtensionSelectionTests` executes attribute, element, collection and constructor-argument forms. |
| Collection replacement followed by additions | `MemberBinder` permits the first value to replace a collection before adding subsequent items. `CollectionReplacementBinder` retains setter-before-adder alternatives for object-valued providers; the selected adder reads the collection after the provider runs. Shared `CollectionDispatchTests` verifies replacement, getter timing, attached properties and explicit/runtime null behavior. Like the pinned SRE compiler, generic lists retain the non-generic `IList.Add` fallback, including its incompatible-item exception. |
| Runtime collection overload selection | `DynamicCollectionBinder` retains ordered `Add` alternatives for object-valued extensions; `DynamicAddEmitter` emits shared typed dispatch helpers. `DynamicCollectionTests` covers keys, null/nullable items, object fallbacks, explicit interfaces, single evaluation and unmatched-value failures. Shared regressions verify that static adders read the getter for every item before its value, while runtime alternatives evaluate the key and value first and read the getter only for a matching adder. |
| Static collection overloads and conversions | `MemberBinder` preserves declared/inherited adder order and gives explicit text conversions priority. `CollectionKeyBinder` converts keys before selecting a value overload. Emission casts arguments to the chosen parameter types so C# cannot select a different overload. Shared `CollectionOverloadTests` covers object/base overloads, primitive conversion precedence, invalid numeric text, key conflicts and converted collection replacement. |
| Delegate-valued properties and root events | `RootMethodBinder` shares accessible method selection between events and delegate values. `RootMethodBindingTests` executes attributes, text/string elements, private partial-class methods, nested constructor arguments and deferred owner capture, and checks invalid method diagnostics. |
| Event delegate expressions | `BoundEventAssignment.Value` carries markup/object-form handlers through emission and tooling traversal. `EventValueTests` verifies event target services, single evaluation, named fields and session cleanup for CLR and attached events. |
| Constructor overloads and argument evaluation | `ConstructorBinder` retains the first directly compatible signature, then the first signature of the same arity for conversion; object-valued arguments cannot silently downcast. `ValueEmitter.EmitArguments` completes arguments in order using their resolved parameter types. Shared `ConstructorDispatchTests` covers overload order, conversion failures, nested markup types and provider/construction ordering. |
| Runtime string conversions | `ValueBinder.TryConvert` binds nonliteral strings through the selected `Parse` method or a typed converter expression. Shared `StringValueConversionTests` verifies static/provider values, member converters, constructor arguments, conversion precedence, collection replacement, read-only attribute conversions and exact culture-overload selection. Bound graph traversal retains converter inputs for tooling and emission. |
| Member-specific text conversions | Property elements, attached getters and constructor arguments retain conversion metadata. String elements and `x:Arguments` preserve their namespace scopes. Static `Parse` takes precedence over type-level converters, while member converters override it. `TextConversionContextTests` executes these paths. |
| Intrinsic value-type probing | `ValueTypeProbe` shares intrinsic normalization and static-member resolution with binding. Shared `IntrinsicValueTests` covers typed static values, collection replacement and constructor selection. Native `IntrinsicTypeProbeTests` covers arrays, known and forward references, and diagnostics. `x:Array`, `x:Reference` and property-element namespace declarations extend beyond the pinned upstream parser/intrinsics and are tested natively. |
| Intrinsic object/property-element forms | `IntrinsicMarkupBinder` supports `TypeName`, `Member` and `Name` property elements, preserves generic-argument namespace scopes, and diagnoses duplicate/unknown arguments. `IntrinsicObjectTests` covers these forms, forward references, Boolean collection values and framework rule overrides. |
| Inherited content and qualified members | `MemberBinder` retains the metadata-selected content property and resolves qualified properties/events on their stated owner. Emission qualifies receivers when a derived member hides that symbol. Shared `InheritedMemberTests` covers scalar content, attributes/property elements, static/runtime collection getters, collection replacement and event adders. |
| Inherited metadata | `RoslynTypeSystem` explicitly inherits whitespace and initialization metadata, with the nearest initialization flag taking precedence. Converter attributes remain attached to their declaring type/property. Shared `InheritedMetadataTests` covers derived whitespace collections, trimming, initialization overrides, type-converter noninheritance and selected virtual-property converters. |
| Converted reference objects | The portable `XamlTextObjectExpressionRule` uses `Parse` and type converters for text bodies, string elements and string-valued markup children; shared `ObjectConversionTests` executes these forms. The pinned XamlX imperative backend does not compile text-only converted roots: `DefinePopulateMethod` casts the converted value to `XamlValueWithManipulationNode` and throws `InvalidCastException`. These roots are therefore not claimed as an upstream-supported missing feature. |

This is an ongoing source audit. Passing the current suites does not close the remaining work:

- Complete the framework-transform comparison beyond the portable corpus, including Avalonia animation, binding, selector and resource behavior. Existing framework tests and theme gates remain relevant but do not certify untested combinations.
- The Avalonia 12.1.3 transform inventory is still being reviewed, including registered-property special values, named binding sources, runtime source information and the remaining binding/resource transforms.

The native Avalonia suite now also references the pinned `Avalonia.Markup.Xaml.Loader` 12.1.3 package solely for differential tests. `ContainerQueryTests` compares compiled query construction, invalid-input rejection and named-container layout behavior against that compiler. Production projects retain no XamlX or runtime-XAML-loader dependency. The query parser and binder emit typed public `StyleQueries` calls and preserve the pinned compiled transform's height/conjunction behavior, which differs from Avalonia's reflection query parser.

`TemplatePartTests` compares missing/incorrect part diagnostics, inherited and overridden declarations, explicit/inferred target types and nested/outer namescope isolation against the same package. `BoundDeferredExpression.NameScopeId` retains the scope allocated by binding, and the name registry preserves declaration spans for those diagnostics.

`StyleScopeTests` compares derived styles/themes, custom template scopes declared through an interface, object-form and qualified target properties, selectorless owning-control inference, invalid theme scopes and `x:SetterTargetType` precedence against the same compiler. Template property references use those semantic scopes when resolving `TemplateBinding` values.

`StyleWarningTests` adds 30 differential cases for duplicate setters, item containers in data templates and styles in merged dictionaries. These check warning severity/count, negative scopes, successful construction and Avalonia's runtime duplicate-setter exception. The implementation preserves observed upstream restrictions: duplicate detection compares direct-content literal property names; container warnings require a `ContentControl`; target-type metadata wrappers suppress the immediate-parent merged-dictionary check.

`TemplatePriorityTests` adds 16 differential cases covering explicit styled/attached properties, CLR-wrapper bypass, style-trigger overrides, custom template scopes, literal/static/dynamic resources, binding priorities, direct properties, implicit/explicit content and assigned binding values. A native live-edit check also verifies source declarations and the generated setter. `BoundStaticSetter` supplies typed method/descriptor symbols without adding Avalonia names to the portable backend; `BoundMember.IsImplicitContent` preserves the pinned compiler's different handling of implicit content.

`DataTypeMetadataTests` adds 18 differential cases for `x:DataType`, annotated CLR properties, template matching and compiled template bodies. The directive assigns an annotated `DataType` property unless that property was explicitly assigned; in that case the directive still takes precedence for binding scope. Symbol-based member binding retains an inherited annotated property even when an unannotated derived property hides it. All `IDataTemplate` implementations establish a separate data-context type scope, and custom `[DataType]` properties support attribute and property-element values.

`DataContextInferenceTests` adds 21 differential cases for compiled `DataContext` results, property order, nested controls, arrays, reflection boundaries and explicit scope overrides. Binding results are retained during scope inference and reused for assignment, including failed results, so sources are constructed once and diagnostics are not duplicated. The pinned compiler resolves these paths from inherited metadata before the general binding `DataType`/`Source` transform; the tests preserve that precedence and validate ignored type names. A separate native check retains XamlG's existing inference from ordinary object-valued `DataContext` property elements, which extends the pinned compiler.

`ItemTypeInferenceTests` adds 35 differential cases for `[InheritDataTypeFromItems]`: standard/custom templates, assigned display bindings, arrays and generic enumerable interfaces, constructed/static/provided collections, nested item scopes and custom ancestor-selected column properties. `XamlPropertyBindingScope` exposes the owning property to framework rules and restores it after nested binding. Inference reuses cached typed paths in the source owner's object/property scope; dependency discovery preserves Avalonia's inference-before-binding-metadata order even when the collection assignment precedes its consumer. Explicit template types bypass that inference, reflection and non-generic sources cannot supply an item type, and a missing data-context declaration differs from metadata with an unknown type. Runtime checks verify updates and single source/provider construction.

## Run

```sh
git clone https://github.com/AvaloniaUI/XamlX external/XamlX
git -C external/XamlX checkout 7ef6aef496ab6e8dcf3df04bef697be49db37c04
dotnet test tests/XamlG.XamlX.Baseline.Tests -c Release --filter 'Category!=ParityRegression'
dotnet test tests/XamlG.XamlX.Compatibility.Tests -c Release --filter 'Category!=ParityRegression'
dotnet test tests/XamlG.XamlX.Baseline.Tests -c Release --no-build --filter Category=ParityRegression
dotnet test tests/XamlG.XamlX.Compatibility.Tests -c Release --no-build --filter Category=ParityRegression
```

The CI workflow retrieves the revision from the same props file and runs the two suites in separate jobs. TRX results distinguish the baseline from the replacement compiler.

## Theme checkpoint

At compiler commit `57238d2`, the theme gate passed against Avalonia `8eeda4f6f546165b3f72e63c9f42247abb306905`: 82 Simple documents (81 physical plus one declared project link), 86 Fluent documents, original code-behind, and 34 control/theme realizations per theme. Both themes were compiled from a clean detached compiler worktree; retained source hashes and source copies match the pinned checkout. Local evidence and compiler/upstream provenance are under `artifacts/tests/57238d2/`.

The same compiler checkpoint passed 760 native tests, 217 upstream compatibility tests and all 85 shared parity cases through XamlG, with no skipped tests and warning-free solution/compatibility builds. This validates that commit, not subsequent compiler changes or the full framework transform surface.
