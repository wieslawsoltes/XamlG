# Resource snapshot cache fast paths

This pass follows compact DAG-aware resource discovery. It changes cache access,
cache-entry admission and redundant edge bookkeeping, not the scanner's child
grammar, URI compaction, graph outcomes, generated source or runtime execution.

## Avoid captures on warm paths

The previous `Get` method contained a factory lambda capturing its cancellation
token. Although factory invocation was conditional on a weak-cache miss, placing
the capture in the public lookup method imposed a display-class allocation on
ordinary calls. Move factory creation behind two private cold methods:

- Warm reads use `TryGetValue` and return the existing immutable snapshot.
- An uncancelable cold miss uses a noncapturing static factory.
- A cancelable cold miss creates a local capture in its own private method, keeping
  its cancellation token visible to the scanner without retaining it in the cache.

The initial and final cancellation observations remain. A canceled cold computation
cannot publish partial dependency state. Concurrent successful factories still
publish through the existing `ConditionalWeakTable` operation; the cache does not
retain a factory, callback, token source, document or scan state in its snapshot.
The exact reference identities and order of dependency representatives do not change.

This is not global pooling or cross-compilation interning. The weak root determines
snapshot lifetime, and cached negative results for nonempty roots remain useful.

## Empty roots do not need cache entries

A bound root with zero arguments and zero assignments is immutable and cannot
contain a resource dependency. Return the initialized shared empty array directly,
without creating a weak-table entry. Preserve cancellation checks around this path.
No type symbol, constructor callback, provider or external state is consulted.

Only this syntactically empty IR shape bypasses storage. A nonempty root that scans
to no resources still caches its negative result, avoiding repeated discovery. New
root records with assignments cannot be confused with earlier empty roots. Rootless
documents preserve their existing empty result and initial cancellation check.

This eliminates per-root weak-table bookkeeping for empty roots while callers hold
them alive. It does not claim a fixed entry-count limit for all other roots or a
reduction in the size of the input IR itself. Dependency discovery remains expected
O(V + E) on a cold nonempty root; warm lookup remains expected O(1). Cold factory
routing adds small branches/calls and retains a capture when cancellation is enabled.
Those costs must be measured alongside warm-path improvements.

## Consume the unique edge contract directly

Compact snapshots already contain one first-occurrence expression per ordinal URI.
The local URI index maps each unambiguous URI to one document index. A document has
only one declared resource URI, so different unambiguous URI keys cannot map to the
same index. Therefore caller/target pairs are already unique before reverse-graph
construction. Remove the redundant per-target integer stamp array and its writes and
checks. This saves one O(D) integer buffer whenever the graph has local dependencies,
without changing expected O(D + U) graph work or diagnostic order.

Do not generalize this removal to arbitrary occurrence lists: the unique snapshot
contract is required. A future change that introduces aliases mapping different URI
keys to one index must restore target deduplication or change the contract. Tests
explicitly cover ordinal case distinctions, ambiguous duplicate declarations and
multiple target documents sharing the same bound root and local descriptor ID.
Current membership, cycles and backend failure closure are still recomputed.

## Quality gates

Fourteen added C# cases include three independent graph-oracle comparisons and
eleven cache cases. They check warm positive and rootless allocation, empty-root
nonpublication, canceled cold/warm reads, default/cancelable factory routes,
first-reference identity and root isolation, concurrent publication and collection
of a cold cancellation source. Reflection used to inspect the private weak table
is outside allocation measurements; it verifies absence of entries rather than
inferring retained memory from elapsed time.

The existing frozen reference/graph oracles, seeded DAGs, filter saturation,
cancellation recovery and generated resource execution tests remain mandatory.
In particular, dependency metadata must not deduplicate runtime include calls.

`ResourceSnapshotProbe` adds twelve workload names to the existing 172-name manifest
(total 184). It measures batches of 1,000 calls for empty, nonempty-negative and
positive roots under default and active cancelable tokens. Warm batches reuse a live
root; cold batches include fresh root/document record copies. Type-system/model,
reflection-delegate and token-source setup is untimed. Both revisions run the same
probe via the existing internal graph-reference method signature. These are graph
metadata microbenchmarks, not cold processes or complete builds.

The signature includes every selected URI, local-document identity, generated
namespace and source span, plus cancellation outcomes. Existing source, diagnostics,
mappings, runtime, full-catalog and captured-C# signatures remain unmodified.

Reproduce from a clean candidate checkout with the repository SDK:

```sh
dotnet test XamlG.slnx -c Release -p:NuGetAudit=false
python3 scripts/test-resource-dependency-model.py
python3 scripts/test-compiler-performance-gates.py
python3 scripts/verify-workflow-inventory.py
python3 scripts/compare-fast-paths.py --baseline-ref 28725d69168c2af3417fba049ad3817efea08e87 --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py --baseline-ref 28725d69168c2af3417fba049ad3817efea08e87 --iterations 3
python3 scripts/verify-compiler-performance.py \
  --baseline 28725d69168c2af3417fba049ad3817efea08e87 --candidate "$(git rev-parse HEAD)" \
  --require-identical-sources --workload-manifest tools/XamlG.PerformanceProbe/workloads.json
```

Read actual exact-head measurements and residuals in the PR evidence. No speedup,
zero-allocation result, whole-build improvement or XamlX parity is established
until native execution and the complete paired evidence gate have succeeded.
Keep all native/MSBuild platforms, original themes, upstream, package consumers,
workspace, host, full ControlCatalog and all browser shards enabled.
