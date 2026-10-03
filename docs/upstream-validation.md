# Upstream validation

The revision pins are in `tests/Upstream.props`. Production packages have **no dependency on XamlX**.

## Two distinct suites

`XamlG.XamlX.Baseline.Tests` runs the pinned upstream SRE/IL compiler and all 222 test cases available in that build configuration. The only harness adjustment is locating the runtime interfaces inside the combined test-reference assembly. This is the baseline, not a test of XamlG.

`XamlG.XamlX.Compatibility.Tests` routes compilation through XamlG syntax, Roslyn binding, typed C# emission and Roslyn assembly emission. It runs 217 upstream runtime/diagnostic cases: 198 linked test cases and 19 dynamic-setter cases. Test bodies and assertions are retained. A test-only source generator replaces the IL-specific dynamic-setter configuration with the public XamlG setter-provider extension point. Exception categories and legacy diagnostic IDs are translated at the harness boundary.

Five baseline cases are not counted as XamlG compatibility tests: four `ParserTests` cases assert XamlX's internal AST representation; `Dynamic_Setter_For_Public_Property_Should_Be_Shared` asserts an IL-only helper metadata name. C# output has different internal types and helper identifiers. These exclusions are explicit, not silently skipped successes.

The suites do not cover every custom transformer used by every XAML framework. In particular, passing the portable XamlX tests does not establish full Avalonia styling, selector or compiled-binding compatibility.

## Run

```sh
git clone https://github.com/AvaloniaUI/XamlX external/XamlX
git -C external/XamlX checkout 7ef6aef496ab6e8dcf3df04bef697be49db37c04
dotnet test tests/XamlG.XamlX.Baseline.Tests -c Release
dotnet test tests/XamlG.XamlX.Compatibility.Tests -c Release
```

The CI workflow retrieves the revision from the same props file and runs the two suites in separate jobs. TRX results distinguish the baseline from the replacement compiler.
