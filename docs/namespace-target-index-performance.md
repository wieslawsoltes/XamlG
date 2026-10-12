# Namespace-target indexing

`ResolveNamespaceTargets` previously filtered the complete namespace-mapping array into a temporary list for every uncached general XML namespace. For Q distinct namespaces and M mapping records this performs O(Q * M) matching work before assembly expansion. The new implementation performs at most three full scans, then builds an immutable ordinal index in O(M), yielding expected O(M + Q + R) matching work, where R counts matching records consumed by the queries. URI hashing/comparison and assembly expansion are additional work. Configurations of at most 32 mappings remain scan-only; their scan cost is bounded by 32 comparisons per query.

The existing namespace-target/result caches remain unchanged. Direct `using:` and `clr-namespace:` namespaces bypass index admission. Their temporary mapping objects and lists are removed, immutable assembly enumeration is unboxed, and an empty target result needs no builder or backing array.

## Ownership and ordering

The index stores heads and one-based integer links into the original immutable mapping array. Reverse insertion produces original declaration order for interleaved groups. It never sorts or deduplicates target probes, and it does not change assembly simple-name normalization, first `assembly=` selection, case sensitivity, forwarder lookup or access checks. Null declared namespace values cannot match a successful request and are omitted from the index, without changing the public mapping array.

All-unique groups require no link array. A second record for a namespace allocates one M-element integer link array; there are no per-group lists or copied mapping records. The index lives with its type system, retains only configured URI keys, and has no process-static roots. Concurrent first builders may temporarily construct equivalent indexes; compare-exchange publishes one fully initialized immutable index. Replacement configurations/compilations get separate indexes.

This trades retained index memory for repeated-query work. Up to three general queries allocate no index. Beyond that threshold, dictionary storage is O(number of URI groups) and optional link payload is 4*M bytes, excluding headers and runtime representation costs. Allocation counters in the probe do not measure retained process heap. Existing target arrays and their cache remain necessary.

## Validation and reproduction

Twelve C# cases compare exact ordered targets against an independent frozen resolver. They cover empty and 31/32/33-sized configurations, delayed admission, no-link unique groups, repeated and interleaved records, empty/null/case-sensitive namespaces, assembly grammar, concurrent publication, replaced configuration and real standalone/shared generated factories instantiated twice.

Twenty probe workloads retain all 364 predecessor workloads (384 total). Warm private-core tests isolate target selection over 8/32/64/1024 mappings; public cold tests include new type-system setup over warmed Roslyn with one or many distinct URI queries. Complete 64/256-child cold binding tests emit and compile their successful fixtures outside timing. Grouped mapping tests cover link traversal and misses. Every returned target's ordinal prefix and assembly identity contributes to the cross-revision signature, as do generated source, mappings and diagnostics.

Run the unchanged native/MSBuild, package, upstream, original-theme, host, workspace, catalog and browser gates. Run `scripts/compare-fast-paths.py --baseline-ref <base> --pairs 3` and the full/captured-C# catalog comparison, then require the committed manifest and 390 byte-identical generated files through `scripts/verify-compiler-performance.py`. Same probe code must run against both revisions. Report index setup, grouped and small-case costs next to gains, with no claim of universal build improvement or XamlX parity.
