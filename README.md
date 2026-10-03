# XamlG

### A Roslyn-native XAML compiler — from source to tools to pixels.

[![Build and test](https://github.com/wieslawsoltes/XamlG/actions/workflows/ci.yml/badge.svg)](https://github.com/wieslawsoltes/XamlG/actions/workflows/ci.yml)
[![Cross-platform solution](https://github.com/wieslawsoltes/XamlG/actions/workflows/solution.yml/badge.svg)](https://github.com/wieslawsoltes/XamlG/actions/workflows/solution.yml)
[![Upstream compatibility](https://github.com/wieslawsoltes/XamlG/actions/workflows/upstream-compatibility.yml/badge.svg)](https://github.com/wieslawsoltes/XamlG/actions/workflows/upstream-compatibility.yml)

**[Compiler Studio](https://wieslawsoltes.github.io/XamlG/)** · [Browser guide](docs/playground.md) · [Language server](docs/language-server.md) · [Validation](docs/validation.md) · [Upstream comparison](docs/upstream-validation.md)

XamlG compiles XAML into inspectable, strongly typed C#. The portable compiler consumes Roslyn symbols directly: it does not execute application assemblies, rewrite their IL, or call XamlX as a production fallback. Build-time generation, source-first designer operations, CLI, workspace, language-server and browser hosts share the compiler pipeline.

> **Development preview.** The implemented hosts have executable validation. The pinned portable compatibility suite is not a claim of complete Avalonia/XamlX replacement. Advanced framework transforms, structural hot reload and a complete visual designer remain distinct work.

## Start from source

.NET SDK **10.0.401** is pinned in `global.json`; package versions are centralized in `Directory.Packages.props`.

```sh
git clone https://github.com/wieslawsoltes/XamlG.git
cd XamlG
dotnet build XamlG.slnx -c Release
dotnet test XamlG.slnx -c Release --no-build
```

The default solution contains the production libraries, CLI/LSP executables and local tests. It does not require a WebAssembly workload or an upstream checkout. The browser has a separate `XamlG.Browser.slnx`; pinned upstream fixtures are opt-in.

## One compiler pipeline

```text
XAML buffer → immutable syntax → Roslyn symbol binding → typed operations → C#
                   ↕                     ↑                    ↓
          revisioned source edits   framework profiles   runtime object graph
                   ↑                                          ↓
          designer / editor / LSP                 Avalonia preview + inspection
```

| Component | Responsibility |
| --- | --- |
| `XamlG.Syntax` | Source-preserving syntax, resilient parsing, scoped namespaces, precise spans and incremental subtree reparsing. |
| `XamlG.Roslyn` | Compilation-scoped symbol resolution, XML namespace metadata, generic types and member lookup. |
| `XamlG.Compiler` | Construction, conversion, directives, collections, runtime-service contracts and extensible typed operations. |
| `XamlG.CSharp` | C# lowering, generated partial classes, direct calls, source mappings and runtime-service adapters. |
| `XamlG.Frameworks` | Symbol-based framework policies, keeping Avalonia conventions outside the portable compiler. |
| `XamlG.Runtime` | Target-local services, namescopes, deferred references, runtime graph and reload primitives. |
| `XamlG.Generator` | Incremental source generator and packaged MSBuild integration. |
| `XamlG.Tooling` | Shared analysis, semantic navigation, source-first designer transactions, bounded undo/redo and minimal buffer diffs. |
| `XamlG.Workspaces` | Roslyn Project snapshots and explicitly trusted MSBuild project/solution evaluation. |
| `XamlG.LanguageServer` | Bounded JSON-RPC transport, versioned document synchronization and semantic LSP handlers. |
| `XamlG.AvaloniaRuntime` | Binding adapters and inspection of realized Avalonia visual trees. |

Core compiler/tooling libraries target .NET Standard 2.0. Workspace, language-server, Avalonia runtime and executable hosts target .NET 10.

## Compile as a library

The caller supplies a `CSharpCompilation` containing the application's source and metadata references, including the appropriate XamlG runtime and framework dependencies.

```csharp
using System;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Frameworks.Avalonia;
using XamlG.Syntax;

public static class XamlCompilation
{
    public static CSharpCompilation AddDocument(
        CSharpCompilation compilation,
        string source,
        string path,
        CancellationToken cancellationToken = default)
    {
        var syntax = XamlSyntaxTree.Parse(source, path, cancellationToken);
        var document = new XamlCompiler().Bind(
            syntax, compilation, AvaloniaFrameworkProfile.Create(),
            cancellationToken: cancellationToken);
        var output = new CSharpEmitter().Emit(document, cancellationToken);

        if (!output.Success)
            throw new InvalidOperationException(
                string.Join(Environment.NewLine, output.Diagnostics));

        var generated = CSharpSyntaxTree.ParseText(
            output.Source,
            new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Preview),
            output.HintName,
            cancellationToken: cancellationToken);

        return compilation.AddSyntaxTrees(generated);
    }
}
```

The generated source can be inspected, passed back to Roslyn for diagnostics, emitted as an assembly, or consumed by a build host. Analysis itself does not execute the generated UI.

## Command-line and editor hosts

```sh
# Compile the checked-in portable example and emit an assembly.
dotnet run --project tools/XamlG.Cli -c Release -- \
  compile --file tests/CliSmoke/View.xaml --code tests/CliSmoke/Model.cs \
  --framework Portable --output artifacts/generated --emit-assembly artifacts/generated/View.dll

# Produce JSON syntax, typed-operation and generated-code inspection.
dotnet run --project tools/XamlG.Cli -c Release -- \
  inspect --file tests/CliSmoke/View.xaml --code tests/CliSmoke/Model.cs --framework Portable

# Start the stdio LSP host for an editor client.
dotnet tools/XamlG.Lsp/bin/Release/net10.0/XamlG.Lsp.dll \
  --code tests/CliSmoke/Model.cs --framework Portable
```

The LSP host supports metadata-only inputs and trusted project mode. `--project` requires `--trust-project`; MSBuild evaluation and application source generators are not a sandbox. See the [protocol surface and current limits](docs/language-server.md).

## Browser Compiler Studio

The browser runs the production .NET/Roslyn compiler, Monaco editors and a real Avalonia browser view. It includes XAML/C# editing, generated C#, syntax and bound-tree inspection, realized visual-tree inspection, source-based property editing, undo/redo, draft restoration, exports, dark/light themes and responsive layouts.

Compile and Run capture the current editor buffers before analysis, rather than relying on a delayed change callback. Buffer changes become minimal source replacements, allowing local subtree reparsing; the Pipeline view exposes parser-work counters. Those counters do not imply that string construction, line indexing or semantic compilation is sublinear.

Run is explicit and executes trusted code in the current browser tab. There is no worker or separate-origin execution sandbox. See [local publishing, execution limits and deployment](docs/playground.md).

## Validation, not assumed parity

The upstream comparison runs **222 pinned XamlX baseline cases** with its original backend and **217 XamlG compatibility cases** through generated C# execution. These are distinct suites. CI checks exact executed counts and rejects skipped cases. Five baseline cases are explicitly outside the source-backend comparison: four internal AST-shape tests and one IL-helper-name assertion.

Additional validation covers compiler/source-generator behavior, packaged MSBuild consumption, designer and language-service operations, real Avalonia controls, stdio protocol exchanges, standalone CLI emission, and browser interactions. See [validation and provenance](docs/validation.md).

## License

MIT. See [third-party notices](THIRD-PARTY-NOTICES.md). Optional upstream test assemblies are not production dependencies or publishable packages.
