# Ordered resource reverse-graph storage

This pass follows PR #38 (`e92e5730b244d6bac0a8f8a5614881e24b7d8071`).
It changes transient graph representation and topological state, not resource
reference discovery, URI resolution, generated source or runtime execution.

## Packed ordered edges

The previous reverse graph contained one nullable List reference per document.
Every referenced target allocated a List and at least a four-element backing array,
even when only one caller existed. Chains, trees and small disconnected components
therefore allocated many small objects that were discarded after validation.

ResourceReverseEdges uses one target-endpoint array, a four-entry initial block,
and lazily allocated 32-entry continuation blocks. An edge contains the original
resource-expression reference, its caller index and the next edge index. Target
endpoints contain the first and last links. Links are one-based: zero means absent
and fresh arrays need no sentinel-fill pass. Appending preserves per-target order.
Capacity is reserved before the new link becomes reachable.

This is indexed linked adjacency in bounded backing blocks, not CSR: edges for one
target need not occupy adjacent slots. Enumeration follows integer links using a
pattern-based value-type cursor, without iterator/interface allocation. Both graph
validators consume the same storage. Direct container use preserves even repeated
parent/descriptor values; production URI compaction is a separate responsibility.

Continuation blocks never resize or copy their edge records. Only the small directory
of block references grows geometrically. There are at most 31 spare edge slots,
rather than up to an entire spare geometric edge array. Indexing is constant-time:
the first four links address the initial block, then a shift/mask selects the block
and slot. The directory and edges are retained only for this validation call.

Construction and complete graph walks remain expected O(D + E), excluding URI
character hashing and dependency discovery. Storage remains O(D + E). Add is
amortized constant-time; directory growth copies references, not edge records.
Bounded blocks introduce an extra indexing indirection and more small allocations
than a single growable array. They avoid its full-edge copies and concentrate spare
capacity in at most one block. Architecture-dependent field/reference sizes and
concentrated fan-in costs must still be measured, not assumed universally better.
The graph is build-local, unpooled and uncached; no input or project state gains a
longer lifetime. Integer guards and runtime array/allocation limits remain in force.

The first candidate used one geometrically grown edge buffer. Its exact-output gate
passed, with lower chain/fan-out allocation, but lattice allocation increased and
cold concentrated fan-in was slower. The final blocked representation responds to
that evidence rather than hiding it. The original measurements remain in PR #39.

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

Twenty-four C# cases cover empty targets, interleaving, initial-block/directory growth boundaries, bounded noncopying blocks,
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
