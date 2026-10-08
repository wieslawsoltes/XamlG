# Avalonia literal parsers

The original files were imported unchanged from Avalonia commit
`a9429a328057befa287ffb5e981f58b86a86eda0` in commit `5e53e44`.
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
