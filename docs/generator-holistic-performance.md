# Generator pipeline performance and quality contracts

This round extends the earlier parser/metadata and typed-markup passes across the generator pipeline. It keeps generated C# byte-identical to its baseline while reducing repeated compiler work and runtime registration allocations. Performance results must be read from the exact-head CI artifacts; an algorithmic improvement is not a claim of XamlX parity.

## Pipeline audit and decisions

| Stage | Change or retained correctness boundary |
| --- | --- |
| Additional-file ingestion | Check global/per-file opt-outs before opening text. Disabled framework selection is bypassed. Existing metadata normalization and invalid-boolean fallback remain authoritative. |
| Incremental equality and parsing | Retain full input content/metadata equality and per-file parse tracking. Re-enabling files reads the current snapshot. No hash-only equality or cross-compilation bound-object reuse is introduced. |
| Framework/runtime binding | Cache immutable service contracts under weak type-system/runtime-configuration identity keys. Diagnostics replay exactly once; document namespace maps remain local. Root-signature probes avoid runtime work only when no custom type rule can observe eager diagnostics. |
| Type/member lookup | Add bounded symbol-plus-access-context memoization for Roslyn accessibility checks. Private, protected, nested and constructed-generic cases use Roslyn's actual result. At saturation, misses are resolved normally. Existing provider/content/member-resolution caches remain owned by their compilation. |
| Namespace parsing | Reuse the common span splitter for CLR namespace declarations; match directive local names before allocating prefixes. XML whitespace, lexical case and namespace shadowing remain unchanged. |
| Resource catalog | Cache validated referenced export metadata by weak type-system identity. Compare catalogs using symbol-aware counted multisets: expected O(R), retaining duplicate ambiguity and all factory fields rather than sorting formatted display keys. |
| Dependency graph | Cache immutable bound reference lists by weak root identity. Recompute cycle/failure/factory availability against current documents and outputs. The iterative graph algorithm and first failing edge order remain intact. |
| Project output reuse | Removal-only changes invalidate shared helper layouts even when no resource export changes. Surviving bindings remain reusable. Dirty state survives cancellation until layouts are validated. |
| Loader integration | Scan each C# tree once into syntax-only candidates, retaining one replaceable method-name entry. Rebind candidates against every current compilation. Skip semantic models for irrelevant trees; index class/URI targets instead of scanning documents per call or resource. |
| Emission planning | Retain reference-identity named-field indexing, deterministic shared helper layouts, typed markup lowering and source fingerprints. No observable conversion, descriptor, initialization, source callback, namescope or cleanup operation is reordered. Successful outputs avoid unnecessary diagnostic SourceText allocation. |
| Generated runtime | Share target-free table accessors per property slot; store explicit targets in session-local value bindings. Delegate-only sessions keep their smaller reference-valued dictionary. Pending transactions still capture their original accessor/target when callbacks replace registrations. |

The broad audit does not justify changing every path. In particular, custom rules, C# semantic invalidation, fallback diagnostics, observable argument conversions, trimming contracts and dependency failure closure remain conservative. Fingerprints retain their existing byte identities; no weaker hash substitutes for source equality.

## Cache ownership and invalidation

Runtime contracts use nested ConditionalWeakTable keys for the RoslynTypeSystem and XamlRuntimeConfiguration. Only immutable symbols/configuration diagnostics are retained; BindingContext, source snapshots and cancellation tokens are excluded from values. Competing factories publish one result and each caller receives one diagnostic replay. Per-document namespace collection occurs independently.

Referenced exports are invariant for the type-system owner. The cache stores no local root signatures or custom rule decisions. Loader snapshots contain syntax nodes, not semantic models or bound loader targets; changes in another C# tree, such as a global alias, must rebind the unchanged call tree. Weak tree ownership and a single replaceable method-name slot bound retention across custom profiles.

The accessibility cache is compilation-owned and capped at 16,384 entries. Both symbol and access context participate in equality. A bounded CAS admission counter avoids ConcurrentDictionary.Count locking and does not grow indefinitely after saturation. Negative results are safe only within that immutable compilation/context.

Resource edge snapshots are keyed by immutable bound root identity, allowing diagnostic-only document copies to reuse them. The cache never establishes that an included document is valid or that its generated factory still exists. Every current graph and emission failure is revalidated.

## Runtime allocation tradeoffs

A generated property table lazily publishes one immutable accessor per used slot. The accessor holds only its table/index; it never retains a control. Session dictionaries hold the explicit target. Public registration and exception order stay unchanged, including null-session, invalid-slot and disposed-session cases.

This removes per-target accessor objects but adds one nullable accessor-reference array per table and larger inline values in table-backed dictionaries. Pending mutation entries are larger than before, and mixed delegate/table registrations can allocate two dictionaries. The new probes measure table registration, delegate-only registration and mutation batches independently; an improvement in construction is not evidence of an improvement in every editing workload. Public defensive metadata copies, thread affinity and rollback semantics remain required.

## Quality gates

The existing native/MSBuild matrix (Linux, Windows, macOS), pinned upstream compatibility, unmodified themes, package inspection/clean consumers, host integration, headless/desktop/trimmed-browser ControlCatalog and general browser acceptance remain enabled. The new regression coverage checks cache concurrency/lifetime, configuration changes, stale alias binding, duplicates, source spans, removal/readdition/cancellation recovery, opt-outs and transaction target snapshots.

The identical baseline/candidate probe now covers cold, no-op, one-file and C#-changed projects; runtime contract contexts; accessibility; directive lookup; warm loader scans; actual generator cold/no-op/edit/disabled paths; table/delegate registration; and mutation batches. Project probes use sequential compilation for thread-local allocation accounting. The existing full-catalog comparison retains the generator's normal parallelism and analyzers. Input/model setup and semantic snapshots are outside measured operations. Each pair is a fresh process, alternates AB/BA, and records five batches after warmup.

`verify-compiler-performance.py` rejects incomplete pairs/projects/workloads, invalid samples, mismatched baseline/candidate/SDK identities, tracked source edits and differing semantic snapshots. CI additionally requires identical generated-source inventories, byte counts and SHA256 maps for this pass. A future intentional emitted-code change requires an explicit reviewed change to that policy plus execution/mapping evidence; it cannot silently pass as a cache-only optimization. The gate does not manufacture stable timing thresholds on shared runners. Raw regressions and neutral outcomes must be reported and reviewed.

```sh
python3 scripts/test-compiler-performance-gates.py
dotnet test XamlG.slnx -c Release
python3 scripts/compare-fast-paths.py --baseline-ref <baseline-sha> --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py --baseline-ref <baseline-sha> --iterations 3
python3 scripts/verify-compiler-performance.py --baseline <baseline-sha> \
  --candidate "$(git rev-parse HEAD)" --require-identical-sources
```

Generation time, generated-C# compilation/analysis, runtime construction and total build time are different measurements. Cold/warm cache figures must be labeled accordingly. The repository's separate XamlX comparison remains necessary to quantify the remaining end-to-end gap; these before/after XamlG probes cannot establish parity.
