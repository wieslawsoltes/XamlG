# Compact resource dependency discovery

This pass changes compiler dependency metadata only. It does not change binding,
provider execution, emitted source lowering, runtime factory calls or construction
ownership. The internal reference snapshot is now a dependency view: the first
original `BoundResourceExpression` for each ordinal URI, not every occurrence.
Do not use it as an execution walk, an occurrence count or an editor reference list.

## Why graph planning alone was not enough

Before this pass, `ResourceReferenceCache.Scan` expanded every expression occurrence
and retained every resource occurrence. The subsequent graph planner deduplicated
caller/target pairs, but only after discovery and snapshot allocation had completed.
A compact binary DAG with d shared levels can therefore require 2^d leaf visits;
its positive-reference snapshot can be exponentially larger than its immutable IR.
The earlier O(D + E) graph-planning bound did not bound that preceding scan.

Let V and E count the distinct reachable aggregate IR nodes and their stored edges
for one root, R count expanded reference occurrences in the old traversal, and U
count distinct ordinal dependency URIs. The new expected discovery work is O(V + E),
plus URI character hashing/comparison costs, and the retained snapshot is O(U)
instead of O(R). Input IR is never mutated or compacted: it still owns every original
construction/reference occurrence. Across a project, sum discovery work over roots
whose weak snapshots are not already available; this is not cross-document subtree
memoization.

## Traversal and first-edge preservation

General discovery is iterative. It keeps forward child order on a LIFO work stack,
therefore retaining the previous reverse depth-first reference order. Object and
markup wrappers schedule their underlying `BoundObject` identity separately; merely
tracking wrapper records would not prevent repeated discovery of a shared object.
A choice schedules its extension first, then option/value pairs, then its default,
so the pop order remains default, reversed branches, reversed extension contents.
Other child and assignment shapes reuse the common `BoundTraversal` grammar,
including target descriptors, post-call arguments, initializers and deferred values.
Unsupported/opaque expression shapes remain opaque, as before.

URI compaction keeps the first entire reference object, including its descriptor and
span. It uses exact ordinal strings without normalization, local filtering or a
last-writer rule. Every later occurrence of the same URI is irrelevant to graph
planning: local resolution maps a URI to the same unique document (or the same
missing/ambiguous result), and diagnostics select that URI's first original edge.
The first occurrences of different URIs retain their relative order.

Do not prune while enqueueing children. A child scheduled early can be visited later
than another occurrence because the walk is reverse depth-first. Admission happens
when the node is popped, before its children are scheduled.

## Delayed exact memoization

A full identity set would add an entry for every unshared composite. Instead,
`ResourceVisitFilter` uses a fixed 1,024-bit monotone filter and a lazily created
**exact reference-identity** set. Leaves never enter either structure. Transparent
object/markup wrappers perform constant work per incoming edge; their underlying
objects are the aggregate identities being memoized.

1. If either fingerprint bit was clear, set both and expand without an exact entry.
2. If both bits were already set, add the object to the exact identity set. Expand
   only when that insertion is new.
3. Only an existing **exact identity entry** authorizes skipping children. A filter
   hit alone never does.

For a stable object fingerprint, the first expansion sets both bits. The next visit
must hit the filter and installs or finds an exact entry. Every later visit finds
that entry. Thus every admitted aggregate expands **at most twice**, independent of
filter collisions, saturation, traversal depth and fanout. Two distinct objects with
equal records or colliding fingerprints cannot be confused. This yields expected
linear work with a constant expansion bound; it does not rely on a probability of
correctness. Hash-table lookup retains its usual expected, not adversarial worst-case,
bound.

The filter is an admission optimization, not a cache of truth. Never clear it,
evict exact entries, or change an object's fingerprint during a scan. Any of those
changes would invalidate the expansion bound. Production fingerprints and exact
set hashing use `RuntimeHelpers.GetHashCode`, never recursive record hashing.
Tests can supply a constant fingerprint to force the worst collision pattern.

The fixed filter costs 128 payload bytes plus its array header when needed. Exact
entries are retained for repeated nodes and filter false positives, at most V.
Unshared trees can still incur admission/set overhead, especially after saturation;
there is no universal allocation or time improvement claim. Their negative-resource
cases are explicitly benchmarked. The pending stack is O(E) in the worst case;
the algorithm does not claim O(depth) storage for wide aggregate nodes.

## Small and flat paths

A root with no arguments or assignments returns the initialized empty array without
creating a scanner, stack or visit filter. A single-level preflight recognizes roots
whose arguments are resources/constants and whose assignments are simple sets of
resources/constants. Those roots scan directly in reverse assignment/argument order,
without a traversal stack or visit filter. The preflight is O(immediate root edges),
not constant time; it can add a second pass over a flat root. It stops at the first
unrecognized shape and delegates the complete root to the general grammar.

The first resource is retained inline. Zero or one distinct URI needs no URI hash
set or immutable-array builder. The second distinct URI promotes to ordinal hash
membership and an ordered builder. Repeated references to the first URI do not
trigger promotion. A singleton published snapshot still owns its one-entry array.

## Cache ownership and invalidation

The existing `ConditionalWeakTable<BoundObject, Snapshot>` remains the sole persistent
owner. The visit filter, exact set, mutable URI set, builders, pending stack and
cancellation token are scan-local and are never stored in a snapshot. Completed
immutable snapshots may be shared by diagnostic-only document copies and concurrent
readers. Root changes cause a fresh scan. Canceled scans cannot publish partial state;
a fully completed concurrent computation may still be published, as with the existing
weak cache.

All URIs, including currently external or unavailable ones, remain in the snapshot.
Current local membership, duplicate-URI ambiguity, cycles, prior failures and emitted
factory availability are always recomputed by `XamlResourceGraph`. No validity result
or mutable project ownership state is cached.

## Correctness gates

`ResourceReferenceOracle` freezes the old occurrence-expanding scan. Importantly,
`ResourceGraphOracle` now uses that frozen discovery too, rather than calling the
candidate cache; forwarding both sides through the optimized scanner would conceal
bugs. Existing graph assertions are unchanged.

The 27 added C# cases cover every supported child/assignment shape; reverse ordering;
first descriptor/span identity; case-sensitive and repeated URIs; a 32,768-reference
flat input; depth-128 DAGs with fanout 2/3/5; distinct wrappers over shared objects;
32,768 unary levels; 250 seeded DAGs and full graph/diagnostic comparisons; availability,
ambiguity and failure changes; cancellation; concurrent publication and weak-root
collection. Direct filter tests force all fingerprints to collide and saturate every
bit, checking that every new identity expands and no identity expands more than twice.

Four executable integration cases compile actual two-document resource programs,
with serial/parallel project scheduling and shared/distinct resource IR. Two root
constructions must perform all six leaf constructions and return six distinct values.
These are separate from graph-only fixtures that deliberately use non-executable IR.

The Python model is an independent algorithm check, not C# execution. It covers
1,000 seeded DAGs, 2,000 dependency graphs, collision/saturation, first-edge preservation
and deterministic operation bounds on depth-128 DAGs.

## Benchmark coverage and acceptance

`ResourceDiscoveryProbe` adds 28 cases; the committed manifest requires **172** workloads
and retains all previous 144 cases. The new cases measure cold snapshots and warmed
public graph validation for flat roots of 1/32/1,024 leaves (empty/unique/repeated URIs),
shared DAGs of depth 8/16 with and without resources, unary chains of 32/4,096 levels,
and unshared trees of depth 4/10 with and without resources. Cold operations include
fresh root/document copies; warm validation includes cloning its mutable document
array. Fixture/model setup, reflection delegate binding, correctness and signature
computation are not timed. Both compiler revisions execute the identical probe source.

The internal occurrence-list representation intentionally differs. Discovery signature
comparison therefore uses exact first-reference descriptors/spans per ordinal URI.
Graph diagnostics and backend suppression are compared without normalization. Raw
reports also retain `RetainedReferences` and `UniqueUris`, exposing compaction rather
than hiding it. The separate generated-source, runtime, full-catalog, normal-analyzer
and full/captured-C# comparisons remain mandatory and are not normalized or disabled.
The 390-file byte-equivalence gate is unchanged.

Reproduce from a clean candidate checkout using the pinned repository SDK:

```sh
dotnet test XamlG.slnx -c Release -p:NuGetAudit=false
python3 scripts/test-resource-dependency-model.py
python3 scripts/test-compiler-performance-gates.py
python3 scripts/verify-workflow-inventory.py
python3 scripts/compare-fast-paths.py \
  --baseline-ref 1fcd01dc841d6551a38846990911e84fbea4ee2a --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py \
  --baseline-ref 1fcd01dc841d6551a38846990911e84fbea4ee2a --iterations 3
python3 scripts/verify-compiler-performance.py \
  --baseline 1fcd01dc841d6551a38846990911e84fbea4ee2a \
  --candidate "$(git rev-parse HEAD)" --require-identical-sources \
  --workload-manifest tools/XamlG.PerformanceProbe/workloads.json
```

This candidate has no measured C# speedup yet. Native/MSBuild, workspace, upstream,
original-theme, package, host, Complete ControlCatalog, all browser shards and the
strict paired performance/evidence gate must pass on the actual proposed merge tree.
Review unique-URI, cold, empty and unshared-tree regressions as well as targeted DAG
wins. Synthetic DAG gains do not establish total-build gains or XamlX parity.
