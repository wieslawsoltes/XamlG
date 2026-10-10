# Shared-expression analysis and source metadata emission

This pass builds on merged PR #28 (`8e317e578cb22cc7e6bf927c5fa58407142cbde3`).
It recovers the unpublished round-four lifetime/metadata changes without replacing
PR #28's tested expression sequences, ASCII literal formatting or wide-helper indexes.

## Lifetime analysis

`TemporaryLocalPool` now caches completed non-leaf expression hazards by reference
identity within one emission context. Existing object and assignment caches remain.
A shared acyclic expression graph is analyzed in expected O(V + E) rather than
expanding every occurrence along every parent path. Common constant, reference and
raw-code leaves skip dictionary probes. Cached hits occur before child-enumerator
creation; compound remainders are disposed even when analysis throws.

The cache contains analysis results, not runtime objects or generated code. It does
not deduplicate constructions, calls, side effects, source mappings or registration.
Deferred-reference masking applies to the wrapper result, never its shared child.
A reference visited under a deferred wrapper must still trigger a fixup at a direct
use. Later siblings are still visited after a capture so raw code retains its
conservative document-wide effect on local reuse. Cancellation never publishes a
partial parent result. No global cache retains bound documents or compilations.

The extra dictionary can cost time and memory on unshared composite trees. Both
shared DAGs and unshared trees are measured independently. Recursive analysis is
still recursive; this is not a cyclic-IR or arbitrary-depth stack-safety claim.

## Metadata construction

The fixed 24-character lowercase identifier buffer is stack allocated. UTF-8
encoding, SHA-256 input and the 96-bit prefix are unchanged.

Source declarations retain their first name/digest in local variables. No dictionary
is allocated for zero or one distinct member, including repeated assignments to
that member. A second distinct member promotes the representation to an ordinal
dictionary. Duplicate fingerprints preserve original assignment order; only final
publication is ordinally sorted.

`MetadataRecordEncoding` appends invariant signed decimal fields directly, avoiding
temporary strings for each span, count and text length. Its ten-character digit
buffer is bounded; Int32.MinValue is handled through Int64 before unsigned conversion.
Text lengths still count UTF-16 code units; null is -1, distinct from empty text.
Generated C#, record order and source mappings must remain byte-identical.

## Quality and measurement

The identical-revision probe retains all 45 PR #28 workloads and adds twelve cases:
shared DAG depths 10/16 and unshared-tree depths 8/12, each with literal/reference
leaves, plus 100/1,000-object emission with zero/one property per child. Analysis
measures fresh contexts; fixture binding and reflection-bridge compilation occur
outside timing. Metadata fixtures must compile before measurements and contribute
generated source, mappings and diagnostics to cross-revision semantic signatures.

The committed `workloads.json` pins all 57 workload names. The verifier rejects a
workload omitted from every run, unreviewed extras, and mismatched operation counts
across process pairs. Existing sample completeness, source identities, semantic
hashes and full-catalog byte-equality checks remain mandatory. Fourteen Python
verifier regressions exercise these contracts. Timing is reported rather than gated
by an unstable shared-runner threshold; unshared-tree overhead must be reviewed.

```sh
dotnet test XamlG.slnx -c Release
python3 scripts/compare-fast-paths.py --baseline-ref 8e317e578cb22cc7e6bf927c5fa58407142cbde3 --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py --baseline-ref 8e317e578cb22cc7e6bf927c5fa58407142cbde3 --iterations 3
python3 scripts/test-compiler-performance-gates.py
python3 scripts/verify-compiler-performance.py --baseline 8e317e578cb22cc7e6bf927c5fa58407142cbde3 --candidate "$(git rev-parse HEAD)" --require-identical-sources --workload-manifest tools/XamlG.PerformanceProbe/workloads.json
```

Native/MSBuild platform, upstream, unmodified themes, package consumers, host,
headless/desktop/trimmed-browser catalog and browser/workspace checks are retained.
Use the PR's exact-head CI artifacts for actual results. Local Python checks are
not .NET execution; this document claims no speedup or XamlX parity before evidence.
