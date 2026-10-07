# Upstream validation

The revision pins are in `tests/Upstream.props`. Production packages have **no dependency on XamlX**.

## Two distinct suites

`XamlG.XamlX.Baseline.Tests` runs the pinned upstream SRE/IL compiler and all 222 test cases available in that build configuration. The only harness adjustment is locating the runtime interfaces inside the combined test-reference assembly. This is the baseline, not a test of XamlG.

`XamlG.XamlX.Compatibility.Tests` routes compilation through XamlG syntax, Roslyn binding, typed C# emission and Roslyn assembly emission. It runs 217 upstream runtime/diagnostic cases: 198 linked test cases and 19 dynamic-setter cases. Test bodies and assertions are retained. A test-only source generator replaces the IL-specific dynamic-setter configuration with the public XamlG setter-provider extension point. Exception categories and legacy diagnostic IDs are translated at the harness boundary.

Five baseline cases are not counted as XamlG compatibility tests: four `ParserTests` cases assert XamlX's internal AST representation; `Dynamic_Setter_For_Public_Property_Should_Be_Shared` asserts an IL-only helper metadata name. C# output has different internal types and helper identifiers. These exclusions are explicit, not silently skipped successes.

The suites do not cover every custom transformer used by every XAML framework. In particular, passing the portable XamlX tests does not establish full Avalonia styling, selector or compiled-binding compatibility.

## Implementation audit beyond the pinned tests

Compare behavior against the pinned source as well as its tests. Native compiler regressions cover implementation details. The two upstream harnesses also link 54 additional cases from `tests/XamlG.XamlX.ParityCases` to execute identical assertions through both compilers. These authored differential tests use the `ParityRegression` category and separate TRX files; they do not increase the count of unmodified upstream assertions.

| Upstream behavior | XamlG implementation and evidence |
| --- | --- |
| `ProvideValue` and `ProvideTypedValue` alternatives | `RoslynTypeSystem.MarkupExtensionMethod` shares provider selection across binding and value-type probing. Parameterless providers take precedence; typed returns win within the same parameter shape. `MarkupExtensionSelectionTests` executes attribute, element, collection and constructor-argument forms. |
| Collection replacement followed by additions | `MemberBinder` permits the first value to replace a collection before adding subsequent items. `CollectionReplacementBinder` retains setter-before-adder alternatives for object-valued providers; the selected adder reads the collection after the provider runs. Shared `CollectionDispatchTests` verifies replacement, getter timing, attached properties and explicit/runtime null behavior. Like the pinned SRE compiler, generic lists retain the non-generic `IList.Add` fallback, including its incompatible-item exception. |
| Runtime collection overload selection | `DynamicCollectionBinder` retains ordered `Add` alternatives for object-valued extensions; `DynamicAddEmitter` emits shared typed dispatch helpers. `DynamicCollectionTests` covers keys, null/nullable items, object fallbacks, explicit interfaces, single evaluation and unmatched-value failures. Shared regressions verify that static adders read the getter for every item before its value, while runtime alternatives evaluate the key and value first and read the getter only for a matching adder. |
| Static collection overloads and conversions | `MemberBinder` preserves declared/inherited adder order and gives explicit text conversions priority. `CollectionKeyBinder` converts keys before selecting a value overload. Emission casts arguments to the chosen parameter types so C# cannot select a different overload. Shared `CollectionOverloadTests` covers object/base overloads, primitive conversion precedence, invalid numeric text, key conflicts and converted collection replacement. |
| Delegate-valued properties and root events | `RootMethodBinder` shares accessible method selection between events and delegate values. `RootMethodBindingTests` executes attributes, text/string elements, private partial-class methods, nested constructor arguments and deferred owner capture, and checks invalid method diagnostics. |
| Event delegate expressions | `BoundEventAssignment.Value` carries markup/object-form handlers through emission and tooling traversal. `EventValueTests` verifies event target services, single evaluation, named fields and session cleanup for CLR and attached events. |
| Runtime string conversions | `ValueBinder.TryConvert` binds nonliteral strings through the selected `Parse` method or a typed converter expression. Shared `StringValueConversionTests` verifies static/provider values, member converters, constructor arguments, conversion precedence, collection replacement, read-only attribute conversions and exact culture-overload selection. Bound graph traversal retains converter inputs for tooling and emission. |
| Member-specific text conversions | Property elements, attached getters and constructor arguments retain conversion metadata. String elements and `x:Arguments` preserve their namespace scopes. Static `Parse` takes precedence over type-level converters, while member converters override it. `TextConversionContextTests` executes these paths. |
| Intrinsic value-type probing | `ValueTypeProbe` shares intrinsic normalization and static-member resolution with binding. Shared `IntrinsicValueTests` covers typed static values, collection replacement and constructor selection. Native `IntrinsicTypeProbeTests` covers arrays, known and forward references, and diagnostics. `x:Array`, `x:Reference` and property-element namespace declarations extend beyond the pinned upstream parser/intrinsics and are tested natively. |
| Intrinsic object/property-element forms | `IntrinsicMarkupBinder` supports `TypeName`, `Member` and `Name` property elements, preserves generic-argument namespace scopes, and diagnoses duplicate/unknown arguments. `IntrinsicObjectTests` covers these forms, forward references, Boolean collection values and framework rule overrides. |

This is an ongoing source audit. Passing the current suites does not close the remaining work:

- Audit constructor and nested method argument evaluation order and exact overload preservation. Collection adders and culture-aware Parse calls have shared executable checks.
- Audit inherited metadata and converted root forms against the actual upstream transform/emitter combination before classifying differences as missing features or backend constraints.
- Complete the framework-transform comparison beyond the portable corpus, including Avalonia animation, binding, selector and resource behavior. Existing framework tests and theme gates remain relevant but do not certify untested combinations.

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
