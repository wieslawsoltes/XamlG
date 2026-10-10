# Resource dependency graph allocation

This pass changes compiler bookkeeping only. The pre-existing validator is already iterative O(V + E); no new asymptotic speedup is claimed. The target is its per-document filtered arrays, distinct/hash-set pipelines, URI grouping objects and eager reverse-adjacency containers during cold compilation and repeated project validation.

## Representation and ordering

An ordinal URI dictionary stores a document index for a unique declaration and -1 for an ambiguous declaration. A separate unique count detects the all-ambiguous case. Repeated declarations never make an ambiguous URI valid again. Null resource addresses are omitted; skipped and failed documents participate exactly as in the previous validator.

The existing weak immutable reference snapshots are reused directly. Reverse adjacency, a caller stamp array and optional dependency counts are allocated only after the first resolvable local edge. A stamp stores parent index + 1 at the target's slot. This deduplicates each caller/target pair in constant time without allocating one set per caller. Reverse lists exist only for targets with callers. Backend-only validation does not allocate dependency counts.

Every reverse entry retains the first original edge for its caller/target pair. This is the edge selected by the previous diagnostic lookup. Parent construction order and initial queue order stay in document order. Kahn traversal still identifies both cycle members and documents depending on cycles; the cycle diagnostic selects the first unresolved edge in the unchanged reference-snapshot order. The exhausted queue and visited bits are reused for ordinary failure propagation. Backend failure propagation still suppresses source/mappings transitively while preserving shared sources, layout identity and reusable raw cache entries.

No validity or emission-success decision is cached. The reference snapshot cache is unchanged. Cancellation is checked before planning and while discovering edges, including cache-hit paths. All mutations remain limited to the caller's current document/output arrays, as before.

Complexity remains O(V + E) expected time and O(V + E_unique) planner storage, apart from the pre-existing reference snapshots. URI hashing is ordinal. Reverse entries now carry a reference as well as a parent index; dense arrays are per invocation and are not pooled or retained globally. Tiny, disconnected, high-fanout and duplicate-heavy graphs are measured separately to expose setup/memory tradeoffs.

## Correctness gates

`ResourceGraphOracle` freezes the previous algorithm. `ResourceGraphPlanningTests` compares ordered diagnostics, messages and exact source spans, document/output success, output suppression, mapping removal, unchanged object identities and retained shared-helper/layout metadata. It covers 250 seeded multigraphs with rootless/skipped documents, prior warnings/errors, ambiguous URIs and external references; repeated edge chains; cycle callers; competing failures; cancellation/recovery; and an 8,192-document chain to guard the iterative implementation.

The ordinary solution tests and complete framework/browser/package checks remain enabled. Compiler/reference graph changes do not exempt the existing 390-file generated-source equality gate or the baseline/candidate semantic signature.

## Measurement

`ResourceGraphProbe` adds 20 workloads at 32 and 1,024 documents: cached reference-snapshot validation of disconnected/external-only, chain, fanout, repeated-edge and cyclic graphs; fresh-root validation for disconnected/chain graphs; and backend failure propagation for disconnected, chain and fanout graphs. The workload manifest now pins 85 names.

The same probe source is compiled against baseline and candidate. Reflection binds direct delegates once before timing; no reflection invocation is included per operation. Binding, graph construction and semantic checks are untimed. Warm operations clone the document/output arrays because validation may update their slots. Cold cases additionally clone immutable roots and include their first reference-snapshot scans; those costs are included equally on both revisions. These are intentionally graph-only IR fixtures, not generated programs. Actual generated C# is still compiled and checked by the other probes and full-catalog comparisons.

Each side runs in three alternating process pairs with five measured batches, using the existing fixed-SDK harness and allocation accounting. Exact validation outcomes and backend suppression data join the cross-revision signature. Raw samples, source identities, operation counts and full/captured-C# compiler data must pass the existing evidence verifier.

```sh
dotnet test XamlG.slnx -c Release
python3 scripts/compare-fast-paths.py --baseline-ref <reviewed-base-sha> --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py --baseline-ref <reviewed-base-sha> --iterations 3
python3 scripts/test-compiler-performance-gates.py
```

No timing gain is inferred from the representation alone. PR comments record measurements for their exact candidate/check-out identities, remaining regressions and whether all merge gates have actually completed. Graph-only gains are not whole-build multipliers or XamlX parity.
