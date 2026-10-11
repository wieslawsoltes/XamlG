# Compact resource dependency discovery

This compiler-only pass changes dependency metadata, not binding, provider calls,
generated lowering or runtime ownership. The internal snapshot contains the first
complete `BoundResourceExpression` for each ordinal URI. It is not an execution
walk, occurrence count or editor reference list. Every original include and
construction remains in the immutable IR and generated program.

## Complexity and ordering

The previous scanner expanded every shared occurrence and retained every resource
occurrence. Later graph deduplication did not avoid that preceding work. A binary
DAG with d shared levels could therefore visit 2^d resource leaves.

Let V and E count distinct reachable aggregate IR nodes and stored edges for one
root, R the old expanded reference count, and U the unique ordinal URI count.
Discovery is now expected O(V + E), plus URI character hashing/comparison. The
persistent snapshot is O(U), not O(R). Across documents, sum cold discovery over
roots without snapshots; this is not cross-document subtree memoization.

Forward child insertion onto an iterative LIFO stack preserves historical reverse
DFS order. Object and markup wrappers schedule the underlying `BoundObject`
identity, so separate wrappers cannot defeat shared-object memoization. Choices
schedule extension, option/value pairs and default in their old order. Other
expressions and assignments use the common `BoundTraversal` grammar, including
initializers, descriptors, post-calls and deferred values. Opaque shapes stay opaque.
Admission occurs when popped, not when enqueued, preserving first occurrence order.

Compaction retains the entire first resource expression, descriptor and span. No
URI normalization, local-only filtering or last-writer rule is introduced. Distinct
URIs retain their relative first-occurrence order. Local resolution maps all
occurrences of one URI to the same unique, missing or ambiguous target; graph
failure diagnostics already select its first edge. Current membership, ambiguity,
cycles, binding failures and emitted-factory availability are still recomputed.

## Bounded segments and exact admission

The first measured candidate admitted every aggregate. The 4,096-cast fixture used
154,792 allocated bytes versus 648 before and was 69% slower. The correction carries
a small work budget in pending frames. Casts and arrays with at most two children
may expand without admission while their budget is positive. Unary nodes subtract
one; binary arrays give each child `(budget - 1) / 2`. Other shapes and exhausted
budgets admit immediately. Each admitted outgoing edge starts with budget 31.

A segment contains at most 31 skipped small nodes, not 31 levels of binary
branching. Each aggregate expands at most twice at admission boundaries. The total
work of bounded segments is therefore a constant multiple of admitted outgoing
edges, retaining expected O(V + E). A node can also occur inside multiple segments:
the two-expansion bound is not a bound on every skipped occurrence.

This admits at most 128 identities in a 4,096-cast chain, and at most 32 aggregates
in a depth-10 unshared binary tree, independently of fingerprint collisions. Five
structural native cases assert the retained-entry bounds. Wider pending frames and
more bounded repeated work on shared binary DAGs are explicit tradeoffs. Pending
storage remains worst-case O(E), not O(depth).

At admission boundaries, a lazy 1,024-bit monotone filter reduces exact-set entries
for one-off nodes. If either fingerprint bit is clear, set both and expand. If both
were already set, add to an exact reference-identity set and expand only if new.
A filter hit alone NEVER authorizes skipping. The second admitted visit necessarily
hits the filter and installs/finds the exact entry; every later admitted visit
finds it. Thus at most two admitted expansions hold even with constant fingerprints
or complete saturation. Do not clear bits, evict entries or change fingerprints.
Production hashing uses `RuntimeHelpers.GetHashCode`, never recursive record hashes.

The filter uses 128 payload bytes plus its array header when needed. Exact entries
cover repeats and false positives, bounded by admitted identities. Unique URI
membership also needs temporary storage: the first candidate's 1,024-unique-URI
fixture used 98,304 bytes versus 41,768 before, despite faster discovery. This cost
is not hidden by duplicate-heavy memory savings. Read final-head measurements in
the PR; initial results do not establish the corrected candidate's performance.

## Small paths and ownership

Roots without arguments or assignments return initialized empty arrays without a
scanner. A single-level preflight recognizes only resource/constant arguments and
simple sets of resources/constants. Those roots scan in reverse without stack or
visit-filter storage. Preflight can add a second immediate-edge pass; it stops at
the first unsupported shape and the general scanner processes the complete root.
The first resource stays inline. A URI set and ordered builder appear only on a
second distinct URI; repeated references to the first URI do not promote.

The existing `ConditionalWeakTable<BoundObject, Snapshot>` is the only persistent
owner. Stack, filter, exact set, mutable URI set, builders and cancellation token
are scan-local. Diagnostic-only copies may share completed immutable snapshots.
Root changes scan anew. Canceled or failed scans cannot publish partial state;
concurrent successful computations retain normal weak-table publication semantics.
All currently external URIs remain available for future membership changes.

## Validation and measurement

The frozen `ResourceReferenceOracle` retains the old occurrence scan.
`ResourceGraphOracle` now uses that independent scanner rather than forwarding both
expected and actual results through the candidate. Existing assertions remain intact.

Thirty-two C# cases cover all child/assignment shapes, first descriptor/span identity,
case-sensitive and repeated URIs, 32,768 flat references, depth-128 shared DAGs with
fanout 2/3/5, wrappers over shared objects, 32,768 unary levels, 250 seeded DAG/full
graph comparisons, availability changes, cancellation, concurrency, weak ownership,
forced filter collisions and segment retention. Four executable serial/parallel,
shared/distinct-expression cases require six constructor calls and independent
values across two builds. Graph-only fixtures are not claimed to be executable.

Twelve independent Python tests cover the original exact filter and segmented
algorithm, seeded DAG/graph order, saturated fingerprints and constant-factor work
bounds. These models supplement, not replace, native C# execution.

The probe adds 28 workloads and pins all 172 names, retaining the preceding 144.
Cold/warm flat roots use 1/32/1,024 leaves with empty/unique/duplicate dependencies;
shared DAGs use depths 8/16 with and without resources; unary chains use 32/4,096;
unshared trees use depths 4/10. Cold scans include fresh root/document copies. Warm
validation includes document-array cloning. Model/delegate setup, correctness and
signatures are untimed; both revisions execute identical probe sources.

Discovery signatures compare exact first-URI descriptors/spans, since the internal
occurrence representation intentionally changes. Raw reports separately include
`RetainedReferences` and `UniqueUris`. Graph diagnostics/backend suppression and
all existing generated-source, mapping, runtime, full-catalog and captured-C#
comparisons remain strict. The 390-file byte-equivalence gate is unchanged.

Reproduce from a clean checkout with the pinned repository SDK:

```sh
dotnet test XamlG.slnx -c Release -p:NuGetAudit=false
python3 scripts/test-resource-dependency-model.py
python3 scripts/test-compiler-performance-gates.py
python3 scripts/verify-workflow-inventory.py
python3 scripts/compare-fast-paths.py --baseline-ref 1fcd01dc841d6551a38846990911e84fbea4ee2a --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py --baseline-ref 1fcd01dc841d6551a38846990911e84fbea4ee2a --iterations 3
python3 scripts/verify-compiler-performance.py \
  --baseline 1fcd01dc841d6551a38846990911e84fbea4ee2a --candidate "$(git rev-parse HEAD)" \
  --require-identical-sources --workload-manifest tools/XamlG.PerformanceProbe/workloads.json
```

Require all native/MSBuild platforms, upstream, original themes, packages, host,
workspaces, Complete ControlCatalog, browser shards and strict performance evidence
on the actual merge tree. Review cold, unique and unshared costs alongside DAG wins.
Synthetic gains do not establish total-build improvements or XamlX parity.
