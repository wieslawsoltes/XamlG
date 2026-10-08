# Avalonia literal parsers

The original files were imported unchanged from Avalonia commit
`a9429a328057befa287ffb5e981f58b86a86eda0`: colors in `5e53e44` and animation
parsers/tokenizer in `5e70673`.
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
