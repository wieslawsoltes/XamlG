# Avalonia literal parsers

The original files were imported unchanged from Avalonia commit
`a9429a328057befa287ffb5e981f58b86a86eda0`: colors in `5e53e44` and animation
parsers/tokenizer in `5e70673`, keyboard/cursor parsers in `74204d1`, and
font-feature/shadow parsers and bracket splitter in `3b92450`, and the transform
operation parser in `697d6c0`, and the remaining numeric/font parsers in
`0cb9dbd`, and geometry/effect/cache parsers in `f71e556`, and the decoration parser/enum in `ee72643`.
`upstream.json` records their original paths and SHA-256 hashes.
The MIT license and original color-conversion notices are retained.

These private compiler types parse literals without loading the application's
Avalonia assemblies. The generated application uses its own public Avalonia
constructors and methods; it does not reference these parser types.

Adaptations after the pristine import:

- Move the types into `XamlG.Frameworks.Avalonia.Parsing` and select the upstream
  `BUILDTASK` branches locally, keeping the types internal and excluding brushes.
- Replace the upstream enum-dictionary source-generator dependency with a lookup
  over the compiler's own copied `KnownColor` enum. The numeric entries are unchanged.
- Use the equivalent ordinal string `EndsWith` overload supported by .NET Standard
  2.0, and remove the unavailable nullable-analysis attribute on the name lookup.
- Keep only `MathUtilities.Clamp(double, double, double)` and its exception helper,
  which are the only math helpers used by the color parsers.
- Use an explicit null/empty check for .NET Standard's nullable annotations and
  ordinary `StringBuilder` instances in the retained formatting helpers.

Color grammar, clamping and RGB/HSL/HSV arithmetic remain the upstream implementation.
Literal lowering emits packed ARGB constants for colors and brushes, and numeric
constructor arguments for HSL/HSV values. Public framework parsers are exercised
as the reference in the differential tests.

Animation/tokenizer adaptations:

- Make the copied types internal, move their namespace, and remove type-converter
  attributes and the `AvaloniaObject` base that are unnecessary for parsing.
- Use the equivalent ordinal `EndsWith` overload for cue percentages on .NET
  Standard 2.0; remove the tokenizer's unavailable nullable-analysis attribute.
- Replace the easing subtype factory with typed compiler callbacks. The original
  comma-based spline selection and parser remain; named types are resolved only
  in the referenced framework assembly, without loading that assembly.
- Preserve `KeySpline` constructor semantics, including its acceptance of control
  points that setters reject. Spline easing uses the `KeySpline` constructor
  overload. Preserve cue percentage handling and iteration-count suffix grammar.
- Reuse the upstream tokenizer for numeric literals, removing the duplicate
  token-list parser. Rectangles are lowered to four numeric constructor arguments.

The generated program contains public typed constructor calls and its existing
source metadata. It has no dependency on these private parser types. The
WPF-derived `KeySpline` source link and .NET Foundation license are retained.

Keyboard/cursor adaptations:

- Move the copied enums and parsers into the private compiler namespace. Retain
  the original key aliases, numeric values and parsing order.
- Remove keyboard event matching, platform formatting and device interfaces;
  replace the generic enum parse overload with its .NET Standard equivalent.
- Keep the cursor enum parser while replacing platform cursor construction with
  a private parsed-value holder. Generated code creates the public cursor at runtime.
- Preserve the distinction between XamlX's case-sensitive cursor intrinsic and
  its parser fallback when attaching source metadata. Text-object conversion
  receives its original whitespace before content-property normalization.

Font-feature/shadow adaptations:

- Move the four copied types into the private compiler namespace and keep their
  parsing algorithms, regular expression and constructor count semantics.
- Remove shadow geometry-bound calculations, the unused unsafe modifier and an
  unavailable nullable-analysis attribute. Retained formatting uses ordinary
  `StringBuilder` instances.
- Adapt the bracket splitter to .NET Standard 2.0: `IndexOf` for span membership,
  `Substring` for string construction and enum value 2 for `TrimEntries`.
- Emit ordinary typed C# object initializers for font features and shadows,
  including init-only properties and packed colors. Shadow collections use the
  matching public single-value or first-plus-array constructor.
- Preserve invalid font-feature defaults, range overflow, empty-shadow counts,
  bracket errors, optional-token behavior and fresh reference values. No runtime
  parser or private compiler type appears in the generated application.

Transform-operation adaptations:

- Move the parser into the private compiler namespace. Substitute a recording
  builder and matrix value holder for the framework's types; retain the original
  grammar, unit checks, argument counts and degree/gradian/turn arithmetic.
- Restore the three angle helpers from the previously imported `MathUtilities`.
  The recorder stores ordered calls and numeric arguments without baking matrices
  or loading the application's Avalonia assembly.
- Lower the result into public `TransformOperations.CreateBuilder`, typed
  `AppendTranslate`/`AppendRotate`/`AppendScale`/`AppendSkew`/`AppendMatrix` calls,
  and `Build`. Preallocate the exact operation count. Preserve the shared `none`
  identity and fresh values for other literals.
- Preserve operation kinds and order, including identity operations and signed
  zero, so animation interpolation uses the same primitive transforms. The
  matrix parser's final-value whitespace behavior and rejection of scientific
  notation remain unchanged. Invalid input now produces a source diagnostic
  containing the parser's error during generation instead of failing at runtime.
- Compare both accepted values and interpolation against public framework parsing
  and XamlX, including keyed source metadata. Generated applications contain no
  parser calls or dependency on the private recorder.

Additional numeric and font adaptations:

- Keep the copied `Parse` methods for pixel points/sizes, 3D vectors, relative
  scalars/rectangles and matrices; replace unrelated rendering/math APIs with
  private numeric value holders. Resolve relative units by public enum names.
- Keep all nine matrix components for `Transform.Parse` lowering. This differs
  deliberately from the upstream Matrix XAML intrinsic, which keeps six affine
  components. Partial optional perspective input retains the parser's behavior.
- Retain flex-basis validation and its distinct percentage/absolute numeric styles.
  Replace span range syntax and numeric overloads with .NET Standard equivalents.
- Keep OpenType tag padding, truncation and byte conversion, and the Unicode
  range regular expression, wildcard handling and segment order. Adapt only the
  character membership overload for .NET Standard.
- Retain font-variation grammar, duplicate-axis replacement and tag sorting. Drop
  runtime-only equality, hash caching and formatting from the private parsed
  holder. Generated code calls the public constructors and the shared `Empty`
  value; nonempty settings and mutable matrix transforms remain fresh.
- Suppress object source-info callbacks that the original Parse calls did not
  produce, while preserving keyed resource locations. Invalid parser input is
  reported during compilation with the source span and original parser message.
- Test packaged APIs against their public parsers and XamlX under a non-invariant
  current culture. Flex basis and font variations are additionally tested in
  ControlCatalog.Tests against the newer pinned source build that supplies them.

Geometry, effect and cache adaptations:

- Retain the path parser's command grammar, relative-coordinate arithmetic,
  control-point reflection, implicit repetitions, fill rules, arc flags and
  open/closed figure behavior. Substitute private point/size holders and a
  recording geometry context; generated code calls the public drawing methods.
- Add cancellation checks. Reject a close command followed by unconsumed numeric
  arguments, for which the upstream parser loops indefinitely. Malformed trailing
  exponents also become source diagnostics instead of crashing the generator.
- Emit scoped initialization in typed C#: construct the geometry, acquire its
  disposable context, execute ordered calls, dispose, then return the geometry
  or figures. All four public targets are covered: Geometry, StreamGeometry,
  PathGeometry and PathFigures. PathGeometry/PathFigures use the public
  PathGeometryContext constructor, matching their parsers on both framework
  versions; older PathGeometry.Open does not populate Figures.
- Keep effect tokenization and color parsing unchanged. Replace rendering and
  animation types with private parsed records. Generated effects use fresh
  public ImmutableBlurEffect/ImmutableDropShadowEffect constructors.
- Keep the cache-mode parser's exact, case-sensitive BitmapCache spelling and
  generate a fresh public BitmapCache constructor call.
- Test geometry bounds, contour length, fill/stroke containment with real Skia,
  complete figure/segment values, XamlX source metadata, every command family,
  malformed input and fresh objects. Portable backend tests also cover scope
  disposal after failures, value-type scope mutation and target services.

Decoration adaptations:

- Retain comma tokenization, case-insensitive enum parsing (including numeric
  values), duplicate rejection and order. Replace AvaloniaList and styled objects
  with private list/value holders used only during parsing.
- Run this fallback after the static and list intrinsics. Named single decorations
  keep the framework's shared static collection; compound/numeric forms create
  fresh public collections and TextDecoration objects with typed Location values.
- A shared inventory test discovers public string Parse methods in both supported
  framework builds. Every inventoried literal must bind and compile into C# with
  no direct Parse call. Detailed suites cover the grammar and runtime behavior.
