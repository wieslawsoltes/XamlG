# Ordered resource reverse-graph storage

This pass follows PR #38 (`e92e5730b244d6bac0a8f8a5614881e24b7d8071`).
It changes transient graph representation and topological state, not resource
reference discovery, URI resolution, generated source or runtime execution.

## Packed ordered edges

The previous reverse graph contained one nullable List reference per document.
Every referenced target allocated a List and at least a four-element backing array,
even when only one caller existed. Chains, trees and small disconnected components
therefore allocated many small objects that were discarded after validation.

ResourceReverseEdges uses one target-endpoint array and one geometrically grown
edge array. An edge contains the original resource-expression reference, its caller
index and the next edge index. Target endpoints contain the first and last links.
Links are one-based: zero means absent and fresh arrays need no sentinel-fill pass.
Appending updates the previous last link and preserves per-target insertion order.
The buffer grows before the new link becomes reachable. Growth is checked against
integer overflow; runtime array-size/allocation limits still apply.

This is indexed linked adjacency in a contiguous backing buffer, not CSR: edges for
one target need not occupy adjacent slots. Enumeration follows integer links using
a pattern-based value-type enumerator, without iterator/interface allocation. Both
binding validation and backend-only failure propagation consume the same storage.
Neither callback effects nor generated execution are deduplicated by this container.
It even preserves repeated parent/descriptor values when used directly; production
URI compaction remains the existing separate responsibility.

Construction and all complete graph walks remain expected O(D + E), excluding URI
character hashing and dependency discovery. Storage remains O(D + E). Geometric
resizing gives amortized constant-time insertion, with spare capacity and transient
copies of earlier arrays. A concentrated fan-in target already used one large List;
that shape may see little allocation benefit and pays the new link bookkeeping.
Sparse graphs with mostly one caller per target are the intended memory win.
The graph is build-local, unpooled and not cached. No cross-invocation references,
input IR, project validity or cancellation tokens acquire a longer lifetime.

## Reuse topological counts

After Kahn traversal, a document is processed exactly when its remaining-dependency
count is zero. Every transition to zero queues the document once. All queued nodes
are exhausted before cycle diagnostics. A node in, or depending on, a remaining
cycle has a positive count. Consequently no parallel visited array is needed to
choose the first unresolved edge and its original span.

Only after cycle diagnostics finish are the counts overwritten as zero/one failure
marks. Existing failures are seeded in document order. Per-target caller append order
is unchanged, so competing errors still reach each document in the same order. The
exhausted queue is reused. Backend validation continues to avoid dependency counts
entirely and uses output failure state to suppress duplicate propagation.

Cancellation checks, diagnostic record replacement, failure closure, source-mapping
suppression, shared-helper ownership and cached raw bindings retain their contracts.
There is no mutation of the immutable resource-reference snapshots.

## Tests and evidence

Seventeen C# cases cover empty targets, interleaving and geometric growth boundaries,
exact descriptor identity, repeated values and allocation-free completed enumeration.
Frozen original graph/reference oracles independently compare full diagnostic order,
spans, record identity, mappings and helper preservation for fan-in, fan-out, sparse
islands, lattices, cycles, prior failures and backend failures. Existing seeded
multigraph, duplicate URI, cancellation, deep-chain and generated-execution tests
remain enabled.

Six independent Python model tests include 1,000 adjacency comparisons and 2,000
seeded graph outcomes, checking topological order, first cycle edges and failure
propagation against append-list/separate-visited models. These models do not replace
C# execution.

ResourceAdjacencyProbe adds 18 workloads to the retained 184 (202 total). It measures
warm validation and backend closure for fan-in, sparse islands, four-successor
lattices and dense 16-node DAG blocks at 32 and 1,024 documents, plus cold fan-in.
Both revisions run identical pre-bound validator delegates and inputs. Cold calls
include new root/document records and dependency discovery; warm calls include the
output-array clone but reuse live immutable reference snapshots. Model compilation,
reflection binding, fixture setup and semantic hashes are outside measurements.
Graph-only IR fixtures are not represented as valid generated programs. Existing
full-catalog/fixture compilation, runtime and generated-source checks remain.

```sh
python3 scripts/test-packed-resource-graph.py
python3 scripts/test-compiler-performance-gates.py
python3 scripts/verify-workflow-inventory.py
dotnet test XamlG.slnx -c Release -p:NuGetAudit=false
python3 scripts/compare-fast-paths.py --baseline-ref e92e5730b244d6bac0a8f8a5614881e24b7d8071 --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py --baseline-ref e92e5730b244d6bac0a8f8a5614881e24b7d8071 --iterations 3
python3 scripts/verify-compiler-performance.py \
  --baseline e92e5730b244d6bac0a8f8a5614881e24b7d8071 --candidate "$(git rev-parse HEAD)" \
  --require-identical-sources --workload-manifest tools/XamlG.PerformanceProbe/workloads.json
```

Require exact-head native/MSBuild platforms, workspace, upstream, original themes,
package consumers, host, full ControlCatalog, all browser shards and strict complete
paired performance evidence before merging. Report cold/dense/fan-in and whole-Csc
CPU/wall regressions alongside intended gains. Allocated-byte counters are not
retained-process-heap measurements. No universal improvement, total-build multiplier
or XamlX parity follows from graph-only wins.
