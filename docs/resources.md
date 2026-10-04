# Compiled project resources

XamlG links a project document set before C# emission. `ResourceInclude`, `StyleInclude` and `MergeResourceInclude` resolve to generated or referenced static factories. Neither the compiler nor the generated include path opens a XAML file, performs a network request, invokes a reflection loader, or falls back to XamlX.

## Project layout

```text
View.axaml
Resources/Palette.axaml
Styles/Buttons.axaml
```

```xml
<StackPanel xmlns="https://github.com/avaloniaui">
  <StackPanel.Resources>
    <ResourceDictionary>
      <ResourceDictionary.MergedDictionaries>
        <MergeResourceInclude Source="Resources/Palette.axaml" />
      </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
  </StackPanel.Resources>
  <StackPanel.Styles>
    <StyleInclude Source="Styles/Buttons.axaml" />
  </StackPanel.Styles>
  <Button Classes="primary" Background="{StaticResource Accent}" />
</StackPanel>
```

A resource include requires a ResourceDictionary root; a style include requires an IStyle root. Source must be static and specified once, either as an attribute or Source property element. Relative paths resolve against the including document, root-relative paths against its assembly, and `avares://Assembly/Path.axaml` identifies another assembly. Scheme and authority are case-insensitive; resource paths are ordinal and case-sensitive. Credentials, query strings, fragments, encoded path separators and ambiguous exported identities are rejected.

## Reusable library API

`XamlProjectCompiler` lives in `XamlG.CSharp`, not the source generator. Keep one instance for a project and supply a pre-generation `CSharpCompilation`, immutable syntax snapshots and a framework profile. The project descriptor separates physical diagnostic paths from reproducible logical resource paths.

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.CSharp.Resources;
using XamlG.Frameworks.Avalonia;

public sealed class ProjectCompilerHost
{
    private readonly XamlProjectCompiler _compiler = new();
    private readonly XamlG.Compiler.XamlFrameworkProfile _profile =
        AvaloniaFrameworkProfile.Create();

    public (CSharpCompilation Compilation, XamlProjectCompilation Xaml) Compile(
        CSharpCompilation application,
        CSharpParseOptions parseOptions,
        IEnumerable<XamlProjectDocument> documents,
        CancellationToken cancellationToken = default)
    {
        var result = _compiler.Compile(
            documents, application, _profile,
            cancellationToken: cancellationToken);

        foreach (var document in result.Documents)
        {
            if (!document.Output.Success)
                throw new InvalidOperationException(
                    string.Join(Environment.NewLine, document.Output.Diagnostics));

            application = application.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                document.Output.Source, parseOptions, document.Output.HintName,
                cancellationToken: cancellationToken));
        }

        return (application, result);
    }
}
```

The caller provides actual application/framework/runtime references. Use the project's evaluated parse options, not an unrelated default language version. Keep the pre-generation compilation as the input for the next update; do not feed a previous generated output back as user source.

## Referenced assembly exports

Public classless resources emit `XamlCompiledResourceAttribute` assembly metadata containing their canonical URI and factory type/method. Consumers discover this through Roslyn symbols, without loading the producer assembly into the compiler. Factory namespaces include an assembly identity component, so two assemblies can both contain `Resources/Palette.axaml` without C# type-name collisions.

Cross-assembly references need these exports. Legacy XamlX-compiled resources do not automatically acquire export metadata. Documents declaring `x:Class` are populated through their generated initializer and are not currently exported as resource factories; give reusable dictionaries/styles classless roots. Malformed, inaccessible or ambiguous exports do not become dynamic loader fallbacks.

## Merge and precedence semantics

`MergeResourceInclude` entries must follow other entries in MergedDictionaries. The emitted code constructs each statically resolved source dictionary and merges its entries through typed runtime operations before applying local entries. Later merged entries override earlier merged entries; local entries override merged entries. Same-variant theme dictionaries combine their keys rather than discarding the earlier dictionary wholesale. Ordinary ResourceInclude retains a distinct merged provider.

This implementation performs **factory linking and eager dictionary merging**, not XamlX's IL-level manipulation flattening. It can construct transient source dictionaries. It does not claim identical allocation behavior or benchmark superiority to the IL backend.

Included factories create fresh instances. Their runtime sessions are owned by the calling construction session and are retired with it. A persistent service-provider include stack guards external recursion and excessive nesting without process-wide mutable registries.

## Errors and incremental work

The linker builds a dependency graph and rejects cycles before emission. Documents depending on a cycle fail too, while unrelated documents can still emit. An invalid included document propagates a source-located error to its callers. XG3300–XG3308 cover address/identity errors, lookup, include syntax/type, cycles, dependency failures, merge order and missing runtime contracts.

Project caches retain one Roslyn compilation and one binding/output per logical path. Stable export signatures plus an unchanged syntax snapshot reuse the binding and source. A value-only edit rebinds and emits the changed document, while graph diagnostics are recomputed. Root/export signature changes conservatively invalidate binding assumptions; a new Roslyn compilation or profile/options clears the cache. Dependency errors are never written into the cached raw binding, so a corrected dependency cannot leave stale failure diagnostics in a caller.

`XamlProjectCompilation.Statistics` reports actual bound/reused/emitted counts. These are not wall-clock benchmarks. Catalog indexing, signature comparison and graph validation still perform project-wide work. Cache reuse depends on retaining unchanged syntax and compilation instances.

## Hosts

The source generator, workspace/CLI and browser use the same project compiler. The generator handles empty AdditionalFiles logical-path metadata by falling back to a project-relative file path. The workspace keeps physical paths for editor overlays and logical paths for generated identities. CLI project mode retains the complete document set even with `--file`, so emitted factories and code-behind initializers are not silently omitted.

Compiler Studio's Resources tab adds, edits, removes and inspects resource files. Run emits all documents into one assembly, both in trusted mode and inside the opaque-origin isolated preview. Compile/Run capture the current resource editor before a pending debounce can lose an edit. Project drafts and exports include resource text and all generated files; restoring a draft does not execute it.

The workspace's single-document Analyze API applies one unsaved overlay against its loaded project. Clients with multiple simultaneously edited documents should call AnalyzeProject with the complete overlay set. The stdio LSP does not yet coordinate all independently open overlays as one shared editable resource project.

## Validation

Executable tests compile and run relative and cross-assembly includes, styles, precedence, theme merges, fresh instances and cleanup. Diagnostics tests cover unresolved/dynamic sources, type errors, cycles and dependent failures. Incrementality tests check exact work counts and recovery from failed dependencies. The release gate installs the actual generator package into a multi-file Avalonia application and verifies linked dictionaries/styles at runtime. Browser tests cover resource editing, immediate Run, project export/draft restoration and isolated project execution.
