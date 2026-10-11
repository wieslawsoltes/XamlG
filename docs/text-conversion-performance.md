# Text conversion metadata and tokenization

This pass starts at `85042724b104f3d0786eb78a660e8ee22195d4f5`. It changes ordinary text-conversion metadata discovery and enum tokenization, not generated execution, runtime code, framework conversion rules or the separate member-binding work.

## Pure metadata versus occurrence state

`TextConversionMetadata` is weakly owned by the exact `RoslynTypeSystem`. Its parse-method and attribute-converter entries use exact symbol object identities, not display names or nullability-erasing equality. The outer identity includes the immutable compilation and type-system configuration. The inner weak keys allow transient constructed symbols to die without waiting for a live owner to disappear. Values contain only the selected method/type (including a negative selection); no context, source, text, diagnostics, rule, token or callback is retained.

Parse selection preserves the original predicate and stable descending parameter-count preference, including inherited ordering, implicit result conversion, accessibility and `IFormatProvider`/`CultureInfo` handling. A scan selects the first eligible two-argument method or the first eligible one-argument method. Repeated lookup no longer rebuilds LINQ pipelines or repeats return-conversion classification.

Only attribute-derived converter selection is cached. Every framework provider is called in its original place on every occurrence, its result is revalidated, and every text rule retains its original precedence. A provider can return different converters or emit diagnostics on successive calls. Member converters still precede framework rules and parse methods; type-level converters still follow parse selection. Bound expressions, source spans and runtime conversion results are never reused.

The empty-attribute/configuration path does not create cache state. Converter type-name parsing uses the common span splitter, preserving the old first-comma and CLR whitespace semantics. No runtime type loading or user-code execution is introduced during compilation.

Enum flags use the same span splitter without dropping empty tokens. Non-flags values are never trimmed, flags values are trimmed, duplicate names keep their order, and named lookup remains ordinal. The existing signed-64-bit numeric input/current-culture rules and checked-safe underlying bit patterns are unchanged. Single non-flags values avoid the split pipeline and growable field builder.

## Tradeoffs

Cold and many-unique-type calls create weak entries. Their time and allocation must be reported alongside repeated hits; this is not an unconditional no-allocation optimization. Concurrent misses can compute equivalent metadata independently before one immutable result is published. Cancellation is checked before lookup, while scanning, and before publication; cancellation cannot publish partial selection. Provider/rule callbacks remain outside metadata caching.

The parse candidate scan remains linear in candidate count. Repeated selections are expected constant-time lookups; this does not imply a similar whole-build improvement. Cached data is bounded by live owners/symbols, not a hard entry-count quota.

## Validation

Frozen previous parse/converter selection and enum splitting provide independent oracles. Cases cover inherited ranking, negative selection, nullable and tuple-name distinctions, configuration/replacement compilations, provider/rule effects, cancellation, concurrent publication and weak owner lifetime. Execution tests build identical rows twice in standalone and multi-document modes and verify exact call counts, invariant-culture parse selection, independent runtime values and enum flags.

The same `TextConversionProbe` source runs against both revisions. It binds the pre-existing private metadata entry points once outside timing, then measures cold/warm, repeated/missing/unique and empty-attribute cases. Cold metadata cases include a fresh type system/context over a warmed Roslyn compilation; they are not cold process builds. Complete 32/1,024-child fixtures bind, emit and compile; complete source, diagnostics and mappings join the cross-revision signature. Generated compilation and signature creation are untimed. Existing source/IL/runtime/full-catalog probes are retained.

```sh
dotnet test XamlG.slnx -c Release
python3 scripts/compare-fast-paths.py --baseline-ref 85042724b104f3d0786eb78a660e8ee22195d4f5 --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py --baseline-ref 85042724b104f3d0786eb78a660e8ee22195d4f5 --iterations 3
python3 scripts/verify-compiler-performance.py --baseline 85042724b104f3d0786eb78a660e8ee22195d4f5 --candidate "$(git rev-parse HEAD)" --require-identical-sources --workload-manifest tools/XamlG.PerformanceProbe/workloads.json
```

The manifest contains 126 workloads. Full native/MSBuild platform, workspace, upstream, original-theme, package, host, complete ControlCatalog and all-shard browser gates remain enabled. No result is claimed until exact-head CI and raw comparison evidence have been checked. Report residual regressions rather than inferring XamlX parity from localized cache wins.
