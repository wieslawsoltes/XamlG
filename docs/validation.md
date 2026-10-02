# Validation and upstream provenance

## Immutable inputs

| Input | Revision/version |
| --- | --- |
| Avalonia source | `17350180c33b063f0e98abbfd19aa3cae63f5d56` |
| XamlX source | `7ef6aef496ab6e8dcf3df04bef697be49db37c04` |
| Avalonia runtime packages | `12.1.3` |
| Microsoft.CodeAnalysis | `5.0.0` |
| .NET SDK/runtime | `10.0.401` / `10.0.12` |

The runtime release and source-main baseline are deliberately recorded independently. A test needing a newer source-only runtime API must not be silently skipped or counted as passing.

## Baseline test sets

- `XamlG.Tests`: portable compiler, emitted-code execution, editing, malformed-input handling, dynamic setters and metadata diagnostics.
- `XamlG.XamlX.Baseline.Tests`: the original XamlX parser/compiler suite with its original backend, establishing an oracle.
- `XamlG.XamlX.Compatibility.Tests`: 195 original language test cases linked unmodified and executed against XamlG-generated C#. Covers construction, generics, primitives, collections, dictionaries, markup extensions, converters, service providers, initialization, deferred delegate/function-pointer factories and whitespace.
- `XamlG.Avalonia.Tests`: focused real Avalonia headless tests, including bindings, templates, namescopes, selectors and typed setters.
- `XamlG.Avalonia.Compatibility.Tests`: 46 original Avalonia basic test cases linked unmodified, with the loader routed to XamlG and the platform setup replaced by real Avalonia.Headless. Styled tests use the same SimpleTheme family as the original tests.

The Avalonia compatibility assembly is public-signed with Avalonia's **public** test key to preserve the upstream friend-assembly access contract. No private key is included. The original public key is recorded in the pinned source's `build/AvaloniaPublicKey.props`.

## Honest boundaries

The 195-case XamlX compatibility set is not the 222-case original baseline. Tests coupled to IL-transformer injection or exact IL exception internals are not silently relabeled as source-generator tests. Dynamic-setter behavior has independent executable tests in XamlG.Tests. The generated compiler does not call the original compiler as a fallback.

Additional Avalonia feature families are being integrated. A passing basic suite does not establish parity for compiled binding paths, resource-group transforms, all designer metadata or binary-loader conventions. The feature matrix will advance with executable evidence.
