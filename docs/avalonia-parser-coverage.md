# Avalonia parser transform coverage

The inventory covers the public string `Parse` methods in Avalonia.Base and
Avalonia.Controls, the XamlX/Avalonia language intrinsics, and framework converter
routes that select those parsers. The source pin is Avalonia
`a9429a328057befa287ffb5e981f58b86a86eda0` with XamlX
`d7e37ca63dc9b13cdc95ca165938d4904fa0eddf`. Tests also exercise the packaged
Avalonia 12.1.3 compatibility baseline.

`ParseTransformCoverageTests` has 49 explicit public parser cases. It discovers
public string parser methods and fails on an unlisted type. Each available case
must bind, emit valid C#, and contain no runtime `Parse` invocation. Invocation
syntax is inspected with Roslyn, including escaped identifiers such as `@Parse`.
The same tests run in the native test suite and in ControlCatalog.Tests against
the pinned source build. FlexBasis and FontVariationSettings exist in the latter
but are absent from the packaged baseline.

This inventory is supplemented by behavioral tests; a sample that emits no
parser call alone does not establish grammar or runtime compatibility.

| Value family / public parser targets | Generated representation | Behavioral coverage |
| --- | --- | --- |
| Point, Vector, Size, Thickness, CornerRadius | Numeric constructors, preserving the XamlX intrinsic choices | StructuredLiteralTests, LiteralConstructionTests |
| Matrix | Six affine constructor arguments, matching the XamlX intrinsic even when its parser accepts perspective | StructuredLiteralTests |
| Rect, PixelRect | Numeric constructors | AnimationLiteralTests, existing theme literal tests |
| PixelPoint, PixelSize, Vector3D | Numeric constructors from the copied parser | ParsedLiteralTests |
| RelativePoint | Coordinate constructor and typed unit | StructuredLiteralTests |
| RelativeScalar, RelativeRect | Parsed numeric values and typed unit | ParsedLiteralTests |
| GridLength, RowDefinitions, ColumnDefinitions | Grid unit constructors and ordered typed collections; inherited list separator metadata | LiteralConstructionTests, ListLiteralTests |
| FlexBasis | Validated numeric constructor and basis kind | ControlCatalog.Tests/ParsedLiteralTests |
| Color, Brush, SolidColorBrush | Packed ARGB constants and fresh immutable brushes, matching the XamlX intrinsic's type rules | LiteralConstructionTests, ThemeLiteralCompatibilityTests |
| HslColor, HsvColor | Numeric constructors with upstream color grammar and arithmetic | LiteralConstructionTests |
| Cue, IterationCount, KeySpline, Easing | Typed constructors; named easing types resolved in the referenced framework assembly | AnimationLiteralTests |
| Cursor, KeyGesture | Typed cursor/key/modifier construction; original enum aliases and parsing rules | InputLiteralTests |
| FontFeature, FontFeatureCollection | Typed property initializers and ordered collections; XamlX list grammar takes priority for collections | InitializedLiteralTests, parser inventory |
| OpenTypeTag | Packed numeric constructor, retaining byte truncation and space padding | ParsedLiteralTests |
| FontVariationSettings | Typed variation arrays and public settings constructor; shared Empty identity | ControlCatalog.Tests/ParsedLiteralTests |
| UnicodeRange, UnicodeRangeSegment | Numeric segment constructors and ordered arrays | ParsedLiteralTests |
| BoxShadow, BoxShadows | Typed initializers and public collection constructors | InitializedLiteralTests |
| Transform | MatrixTransform with all nine parsed matrix components | ParsedLiteralTests |
| TransformOperations, ITransform converter | Typed builder calls preserving operation kinds, order and interpolation | TransformLiteralTests |
| Geometry, StreamGeometry | Fresh StreamGeometry, disposable Open scope and direct typed drawing calls | GeometryLiteralTests |
| PathGeometry, PathFigures | Fresh PathGeometry and public PathGeometryContext; return geometry or figures after disposal | GeometryLiteralTests |
| Effect, IEffect converter | Fresh ImmutableBlurEffect or ImmutableDropShadowEffect with numeric/color arguments | EffectLiteralTests |
| CacheMode | Fresh BitmapCache, preserving exact case-sensitive grammar | EffectLiteralTests |
| TextTrimming | Framework static values with original case/whitespace rules | StructuredLiteralTests, parser inventory |
| TextDecorationCollection | Shared single-name intrinsic values; fresh typed decorations for compound/numeric parser fallbacks | StructuredLiteralTests, DecorationLiteralTests |
| Classes | Existing class-property transform or typed list intrinsic, according to the XAML context | ConditionalClassTests, parser inventory |
| FontFamily | Public constructor with the document URI service and family text | Font-family/base-URI tests, parser inventory |

The other language intrinsics are also retained: TimeSpan becomes ticks, Uri
uses its validated public constructor, ThemeVariant and WindowTransparencyLevel
use their framework static members, and arrays/lists recursively lower their
element conversions. Portable primitives and enum literals become typed
constants. Decimal literals preserve their exact bits, including scale and
negative zero. Complete invariant DateTime literals with an explicit date and
no time zone become a ticks/Kind constructor.

Binding paths, selectors, container queries, property references, type names,
markup extensions and class directives are separate syntax/semantic transforms.
Their inventory and behavioral suites are recorded in
[the transform audit](avalonia-transform-audit.md). Internal composition-expression
and Spring parsers are not public XAML literal types. Spring easing continues to
use the upstream supported easing constructor path.

## Compatibility boundaries

FontFamily still resolves the runtime base URI and performs the work required by
its public constructor. Uri construction likewise remains a public BCL operation.
Bitmap/icon loading and culture-dependent or application-defined type converters
retain their runtime services and exception behavior. The compiler does not
execute arbitrary application assemblies to evaluate a custom Parse method or
converter. Explicit property converters keep precedence over framework lowering.

DateTime inputs with missing calendar fields or a time-zone suffix keep runtime
parsing: their value can depend on the current date or runtime time zone. Invalid
DateTime and decimal inputs retain their established runtime exception phase.
Invalid decoration parser fallbacks likewise retain the runtime behavior already
covered by compatibility tests. Other newly lowered malformed framework literals
produce source diagnostics containing their parser error.

The copied path parser accepts and rejects its upstream grammar, including its
scientific-notation rules. A close command followed by numeric input is rejected
explicitly because the upstream loop otherwise consumes no input and never exits.
Cancellation is checked while parsing and lowering. Geometry initialization uses
C# disposal scopes, so a failed call does not leak the drawing context. It uses
the public PathGeometryContext constructor for PathGeometry and PathFigures,
because older Avalonia versions' PathGeometry.Open does not populate Figures.

Geometry tests compare complete figures and segment properties, real Skia
bounds/contour length/fill and stroke containment, XamlX keyed source metadata,
and fresh objects. Portable scoped-initialization tests check evaluation order,
class and struct scopes, target services, and disposal after failures.

All 38 copied source files have their original paths and hashes in
`src/XamlG.Frameworks/Avalonia/Parsing/upstream.json`. Pristine imports are in
separate commits from adaptations. The adjacent README describes each adaptation
and retains the upstream licenses. Private parser/recording types are used only
by the compiler; generated applications use their own public framework APIs.

Removing runtime parsing can increase generated C# size. Source size, generation
time, compilation/analyzer time and the XamlX added-cost benchmark are measured
separately; parser coverage is not evidence that the 2× compilation target is met.

## Validation checkpoint

At implementation `49fce1a`, all 2,385 native tests pass with warnings treated as
errors: 425 core, 1,525 Avalonia, 169 tooling, 94 language-server, 158 automation
and 14 workspace tests. No tests failed or were skipped.

The pinned-source ControlCatalog suite passes all 14 tests, including the same
parser-discovery inventory and the newer FlexBasis/font-variation APIs. All 1,188
catalog cases pass on each of the headless, actual desktop and trimmed browser
hosts. The desktop build and browser publication use the normal compiler and
trimming analysis with warnings treated as errors.

The [source-generation report](source-generation-optimization.md#complete-parser-coverage)
records fresh generated-source counts and separate phase measurements for this
checkpoint.
