# XamlG

### A Roslyn-native XAML compiler, built for tools as well as builds.

XamlG compiles XAML into inspectable, strongly typed C#. Its portable compiler consumes Roslyn symbols directly; it does not load application assemblies into the compiler, rewrite IL, or depend on XamlX in production.

> **Development preview.** The compiler and its first Avalonia integration are executable and tested. Generator, MSBuild, workspace and browser hosts are being integrated. This baseline is not yet an unconditional drop-in replacement for every Avalonia/XamlX feature.

## Compiler pipeline

```text
SourceDocument → lossless XAML syntax → Roslyn binding → typed construction IR → C#
                        ↕                    ↑                  ↓
                   precise edits      framework profiles   runtime graph
```

| Package | Responsibility |
| --- | --- |
| `XamlG.Syntax` | Immutable lossless syntax, resilient parsing, namespace scopes, markup syntax, source spans and transactional text editing. |
| `XamlG.Roslyn` | Compilation-scoped symbol resolution, XML namespace metadata, generic types, member lookup and language conventions. |
| `XamlG.Compiler` | Binding, conversion, construction, collections, directives, service contracts and extensible typed IR. |
| `XamlG.Emit.CSharp` | Deterministic C#, direct calls, source mappings, generated partial classes and runtime services. |
| `XamlG.Frameworks` | Framework-specific symbol-bound behavior. Avalonia types and conventions remain outside the portable compiler. |
| `XamlG.Runtime` | Scoped services, names, deferred references, runtime graph and reload primitives. |
| `XamlG.Generator` | Incremental compiler host boundary; integration in progress. |
| `XamlG.Workspace` | Editor/designer host boundary; integration in progress. |

## Use the compiler as a library

```csharp
var compiler = new XamlCompiler(csharpCompilation, AvaloniaProfile.Create("MyApp"));
var bound = compiler.Compile(xaml, "Views/MainView.axaml");
if (!bound.Success)
    return bound.Diagnostics;

var generated = new CSharpEmitter().Emit(bound);
var compilationWithXaml = csharpCompilation.AddSyntaxTrees(generated.GetSyntaxTree());
```

The input `Compilation` supplies the application's references and source types. Framework services are resolved to symbols rather than selected by scattered string checks. Framework metadata names are centralized in profile contracts.

## Validation

The repository links original tests from immutable upstream checkouts. The baseline XamlX runner and the XamlG compatibility runner are separate: the latter generates, compiles and executes C# before running the upstream assertions. Production projects do not reference the oracle assemblies.

See [validation and provenance](docs/validation.md) for the exact coverage boundary. Passing the linked tests is not a claim that every upstream backend-specific test or every Avalonia transform is covered.

```sh
dotnet test tests/XamlG.Tests
dotnet test tests/XamlG.Avalonia.Tests
python tools/fetch-upstream.py
dotnet test tests/XamlG.XamlX.Baseline.Tests
dotnet test tests/XamlG.XamlX.Compatibility.Tests
dotnet test tests/XamlG.Avalonia.Compatibility.Tests
```

.NET SDK 10.0.401 is pinned in `global.json`. Package versions are centralized. Libraries target .NET Standard 2.0; tests target .NET 10.

## Design principles

Compilation sessions own their caches and Roslyn symbols; no global mutable type caches. Input text is never treated as C# code. Framework hooks bind symbols and emit typed expressions. Precise spans connect syntax, diagnostics, IR and generated code. New behavior is accompanied by executable tests, with one production type per source file.

## License

MIT. See [third-party notices](THIRD-PARTY-NOTICES.md) for the pinned Avalonia selector grammar and external test sources.
