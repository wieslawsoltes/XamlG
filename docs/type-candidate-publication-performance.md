# Type-candidate publication and qualification

This pass changes first-resolution work in `RoslynTypeSystem.ResolveCore`, not its public cache key, namespace selection, accessibility contract or returned symbols. It follows the adaptive source-map pass; the exact baseline and candidate belong to the PR's evidence report.

## Common-case publication

Ordinary type resolution usually finds zero or one distinct accessible symbol. Keep the first symbol inline and promote to the original `HashSet<INamedTypeSymbol>` only when a second distinct symbol is encountered. Missing results still return an initialized empty immutable array. Single results retain a one-element immutable array. Multiple results use the exact previous symbol comparer and ordinal assembly-identity/metadata-name ordering. Forwarders and repeated mappings therefore preserve the first equivalent destination symbol and all ambiguity diagnostics.

Every target assembly is still queried, including the forwarder fallback, and every found symbol still goes through accessibility checking in the original order. The intrinsic XAML language branch is unchanged. There is no new static or instance cache, retained collection, admission heuristic, source context or speculative early return.

Untargeted namespace mappings enumerate contiguous assemblies with the same metadata prefix. Build the qualified metadata name once per contiguous prefix run, rather than once per target. Noncontiguous repeated prefixes are intentionally rebuilt: a second dictionary would add unnecessary setup and retention. This is exact ordinal reuse, not string interning or a change in lookup semantics.

## Bounds and tradeoffs

Target discovery still performs O(T) lookups. With K distinct matches, true ambiguity still incurs expected O(K) set work and O(K log K) publication sorting. Empty and single-result publication use constant auxiliary storage. Qualification copies O(G × L) characters instead of O(T × L), where G is the number of contiguous prefix groups and L the qualified-name length. Unavoidable Roslyn lookup and result allocation remain.

A second match adds an equality check and delayed set setup. All-ambiguous and frequently changing-prefix inputs can benefit less or incur extra branches. The public fully cached path is unchanged. No measured benefit or whole-build speedup is inferred from these bounds; cold and ambiguous costs must be reviewed from the paired evidence.

## Regression coverage

Thirteen C# cases compare the previous set/sort algorithm against candidate resolution. They cover missing/single/multiple matches; untargeted and explicit-assembly namespaces; overlapping mappings; assembly-name case; constructed arity; hidden and nested accessibility; forwarded aliases; concurrent cold resolution; compilation replacement; and actual standalone/shared generated factories constructed twice.

The expected candidate accumulator is independent of the new implementation and reuses only unchanged namespace-target selection. Successful runtime fixtures compile, preserve child values and independent instances, and dispose their sessions. Original source-map, conversion, member, project, graph and runtime tests remain unchanged.

## Reproducible measurements

Fifteen added workloads preserve every predecessor name. Eight pre-bound delegates invoke the existing private core on both revisions, separating uncached candidate publication from unchanged public cache hits. Namespace targets and Roslyn state are warm in these isolated cases; reflection and fixture creation are outside measurement. They include hits, misses, ambiguity, generic ambiguity, duplicate forwarded matches and prefix transitions.

Three public cold batches create a fresh type system over the same warmed Roslyn compilation, then resolve 1, 64 or 256 unique types. This includes type-system setup but is not cold process startup. The warm 256-type public batch exercises existing result-cache hits. Complete 32/256-type cold bindings and a warm 256-type binding provide higher-level context; successful emitted fixtures compile outside timing, and exact generated source, source mappings, diagnostics and ordered candidate identities enter the cross-revision signature.

The same probe source is compiled against both revisions. Require three alternating fresh-process pairs with five batches each, exact source/SDK/harness identities, all **364 pinned workloads**, matching semantic signatures and the complete three-project full/captured-C# comparison with normal analyzers. All **390 catalog-generated files** must remain byte-identical. Allocation counters measure bytes allocated, not retained process heap.

```sh
dotnet test XamlG.slnx -c Release
python3 scripts/test-compiler-performance-gates.py
python3 scripts/compare-fast-paths.py --baseline-ref <reviewed-base-sha> --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py --baseline-ref <reviewed-base-sha> --iterations 3
```

Merge requires exact-head native/MSBuild Linux, Windows and macOS; workspace; upstream; original themes; package consumers; host integration; full headless/desktop/trimmed-browser ControlCatalog; all-shard browser acceptance; and complete strict performance evidence. Report setup, ambiguous, query and whole-compiler regressions beside improvements. This pass makes no claim of universal gains or XamlX parity.
