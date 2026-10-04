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

A resource include requires a ResourceDictionary root; a style include requires an IStyle root. Source must be static and specified once, as an attribute or Source property element. Relative paths resolve against the including document, root-relative paths against its assembly, and `avares://Assembly/Path.axaml` identifies an assembly. Scheme/authority are case-insensitive; resource paths are ordinal. Credentials, query strings, fragments, encoded path separators and ambiguous exports are rejected.

## Reusable library API

`XamlProjectCompiler` lives in `XamlG.CSharp`, not the generator. Keep one instance for a project and supply its pre-generation `CSharpCompilation`, immutable syntax and framework profile. `XamlProjectDocument` separates physical diagnostic paths from logical resource identity. The README contains a complete host example.

```csharp
var compiler = new XamlG.CSharp.Resources.XamlProjectCompiler();
var documents = new[]
{
    new XamlG.CSharp.Resources.XamlProjectDocument(viewSyntax, "View.axaml"),
    new XamlG.CSharp.Resources.XamlProjectDocument(paletteSyntax, "Resources/Palette.axaml"),
    new XamlG.CSharp.Resources.XamlProjectDocument(stylesSyntax, "Styles/Buttons.axaml")
};
var result = compiler.Compile(documents, applicationCompilation,
    XamlG.Frameworks.Avalonia.AvaloniaFrameworkProfile.Create(),
    cancellationToken: cancellationToken);
```

The host provides the syntax variables, actual source/metadata references and cancellation token. Preserve evaluated C# parse options when adding the returned generated sources. Do not feed previous generated trees back as user source on the next update.

## Referenced assembly exports

Public classless resources and eligible code-behind resources emit `XamlCompiledResourceAttribute` assembly metadata containing canonical URI and factory type/method. Consumers discover it through Roslyn symbols without executing producer code. Factory namespaces include an assembly identity component, so separate assemblies can both contain `Resources/Palette.axaml` without C# type-name collisions.

Cross-assembly references need these exports. Legacy XamlX-compiled resources do not automatically acquire export metadata. A concrete nongeneric `x:Class` with a parameterless or single by-value `IServiceProvider` constructor can export a typed factory when generated initialization is enabled. The factory invokes the real constructor. A synchronous thread-local construction scope forwards caller services into `InitializeComponent`, records initialized sessions, and retires them if construction fails. Successful initialization uses the same idempotence guard whether called by the constructor or by the generated factory. Factories always invoke the appropriate generated initializer rather than mistaking a base constructor's session for completed derived initialization. During factory construction, successor sessions own already-initialized base sessions instead of retiring their bindings early. Cleanup failures preserve the original construction exception in an aggregate.

Abstract/generic roots, handwritten initializers, unsupported constructor signatures, unfulfilled required-member constructor contracts, and roots with explicit `x:Arguments`/`x:FactoryMethod` construction directives retain the caller-controlled Populate API instead of receiving an invented constructor call. Matching `SetsRequiredMembers` constructors are eligible. Nested class factories expose separate C# and reflection metadata names; generated `typeof` uses dotted C# names, while browser runtime reflection uses metadata `+` separators. This is not support for bypassing constructors or rewriting user initializer bodies. Malformed, inaccessible or ambiguous exports do not become dynamic-loader fallbacks.

## Merge and precedence

Merge includes must follow other entries in MergedDictionaries. Emitted code constructs each resolved source dictionary and merges through typed runtime operations before local entries. Later merged values override earlier values; local values override merged values. Same-variant theme dictionaries combine keys rather than discarding earlier entries. Ordinary ResourceInclude retains a distinct provider.

This is **factory linking and eager dictionary merging**, not XamlX's IL-level manipulation flattening. It can construct transient dictionaries and does not claim identical allocation behavior or performance superiority to the IL backend.

Factories create fresh instances. Included runtime sessions belong to the caller's construction session and are retired with it. A framework root-provider adapter retains arbitrary caller services through a primary/fallback chain; framework-provided services retain precedence, and initial root-object lookup still consults the original framework provider. A persistent include stack guards external recursion and excessive nesting without process-wide mutable registries. Resource operations cannot be hoisted into expression-only delegates, where eager construction would change lifetime semantics.

## Diagnostics and incremental work

Dependency validation rejects cycles before emission, including callers that depend on a cycle. Unrelated documents can still emit. Invalid included documents and backend-only emission failures propagate source-located diagnostics to callers. Failed emission removes the dependent generated-source closure while keeping raw cached results intact for recovery. XG3300–XG3308 cover addresses/identities, lookup, include syntax/type, cycles, dependency failures, merge order and missing runtime contracts.

Project caches retain one Roslyn compilation and one binding/output per logical path. Stable export signatures plus unchanged syntax reuse binding and source. A value-only edit rebinds/emits the changed document while graph diagnostics are recomputed. Root/export signature changes conservatively invalidate binding assumptions; new compilation/profile/options clear the cache. Dependency errors are not written into cached raw bindings, so a corrected resource cannot leave a stale error in its caller.

`XamlProjectCompilation.Statistics` reports bound/reused/emitted counts, not wall-clock benchmarks. Catalog indexing, signature comparison and graph validation still perform project-wide work. Reuse requires preserving unchanged syntax and compilation instances. `ClearCache` releases retained project state.

## Host consistency

The source generator, workspace/CLI and browser share project compilation. Generator and workspace loading share `XamlInputMetadata`: per-file `XamlGCompile=false` excludes a file, explicit `XamlGLogicalPath` preserves linked-file identity, and empty logical-path metadata falls back to the project-relative path. Workspace loading preserves physical paths for overlays and logical paths for generated identities. Identical duplicate AdditionalDocuments contributed by framework/explicit MSBuild items are coalesced; conflicting text for one physical path is rejected. CLI project mode retains the complete document set even with `--file`, avoiding missing factory/initializer output.

`XamlCompilationSession.AnalyzeOverlays` applies all open XAML buffers as one snapshot over loaded project documents. The stdio LSP uses this for semantic requests and diagnostics. An unsaved included-document edit can fail its caller at the caller's unchanged client version. Closing the edited dependency returns to its loaded on-disk snapshot. Project refresh and the complete open-buffer revision are checked again under the output publication gate, so queued older results cannot overwrite current cross-file analysis.

Unknown/untitled buffers not belonging to the loaded project remain standalone. Watching refreshes on-disk project inputs; LSP edits are unsaved overlays, not implicit file writes.

## Compiler Studio

The Resources tab adds, edits, removes and inspects resource files. Run emits all documents into one assembly in trusted or isolated mode. Compile/Run capture current resource editor text before a pending debounce can lose the latest edit. Replacing a project retires old same-path editor callbacks. Drafts and exports include resource text and all generated files; restoring a draft never executes it.

## Executable validation

Native tests compile/execute relative and cross-assembly includes, styles, precedence, theme merges, fresh instances and cleanup. Diagnostics cover unresolved/dynamic sources, type errors, cycles and dependent failures. Incrementality tests check exact work counts and dependency-failure recovery. The release gate installs the actual generator into a multi-file Avalonia app and verifies linked resources/styles.

`test-lsp-resources.py` starts the real trusted-project host, edits an included resource without saving, verifies caller diagnostics/inspection consistency, closes the buffer and verifies recovery. Browser tests cover resource edits, immediate Run, export/draft restoration, isolated projects and missing dependencies.
