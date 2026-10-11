# Content normalization: linear coalescing and copy-on-change whitespace

Baseline: `1fcd01dc841d6551a38846990911e84fbea4ee2a` (merged member-binding pass).

## Algorithms and allocation

`ContentWhitespaceNormalizer` previously concatenated `previous.Value + fragment.Value`
for every adjacent text node, including runs split by comments and CDATA. For F equally
sized fragments of length K, intermediate copying grew as K * (1 + 2 + ... + F):
O(K * F^2). The new merge phase enumerates input once and uses one StringBuilder only
when a run has multiple fragments. Amortized copying is O(total text + node count),
with one merged record and final string per fragmented run. The first fragment's
CDATA flag and the complete original source range remain intact.

The merged list is compacted in place after normalization instead of allocating a
second list and its backing array. A separate previous-original-node variable ensures
neighbor trimming is evaluated against the original merged sequence, not compacted
or rewritten output. The following node is still untouched at each read. Unchanged
immutable text records are reused; non-text node identity remains unchanged.

Literal element validation formerly rebuilt its immutable namespace scope for each
non-namespace attribute, with each Push scanning all attributes. It now builds one
scope per candidate element. This removes repeated O(A^2) attribute rescanning;
namespace dictionary operations still have their own persistent-collection costs.
No type-binding, string-literal, inherited-attribute or trim-neighbor callback result
is memoized. Their original evaluation order and short-circuit conditions remain.

`XmlWhitespace.Collapse` remains O(text length). It finds the first actual change
before allocating output, returns canonical immutable strings directly, handles
all-whitespace input without a buffer, and copies unchanged character runs on the
changed path. Only XML space, tab, CR and LF are collapsed; NBSP, Unicode separators,
NUL and UTF-16 surrogate code units are preserved. All four boundary trim policies
and `Normalize(..., preserve: true)` retain their behavior.

## Ownership and costs

There is no new static cache, interning table, pool or retained compilation state.
Buffers are local to one normalization call. Source nodes and their immutable
collections are not mutated. Source enumeration remains single-pass and is disposed
on completion, exceptions and cancellation. Cancellation is checked at entry and
during merging/normalization; no partial normalized output is published.

Changed whitespace still needs an output builder/string. Fragmented runs retain
both their temporary builder and final string until the merge phase releases them;
two-fragment/tiny inputs can pay more builder setup than direct concatenation. Input
lists and the final result array remain necessary. The benchmarks cover these cases
rather than inferring universal improvement from adversarial fragment counts.

## Validation

Tests retain frozen copies of the old whitespace and content implementations. They
compare values, CDATA flags, recursive typed syntax/spans, diagnostics, symbols and
stateful type-rule traces. Coverage includes exhaustive short inputs under all trim
policies, long/random UTF-16 text, 200 seeded content sequences, original-neighbor
semantics after compaction, XML-space overrides, unknown attributes, namespace
shadowing, wide literals, single enumeration, cancellation/disposal, concurrency,
standalone/shared generated execution and serial/parallel incremental source edits.

The same public/internal-entry probe is compiled against each revision. Reflection
constructs a typed normalization delegate outside timing; it is not performed for
every call. Direct normalizer timings include list/builder/result allocation, while
context creation, XAML parsing, model setup and signature work are untimed. Mutable
context diagnostic/symbol lists are cleared inside measured calls to avoid accumulating
results. Complete binding timings include a new XamlCompiler call on warmed Roslyn and
preconstructed syntax; they are not cold processes or complete MSBuild runs.

Twenty-nine additional workloads cover canonical/changed/blank whitespace at two
sizes, 1/2/32/256/2048 text fragments with collapse/preserve semantics, complete fragmented
binding, 8/128/512 XML attributes, ordinary element sequences and canonical-text
binding. Every generated fixture must compile. Recursive normalized-node, diagnostic,
symbol, generated-source and source-mapping bytes enter the cross-revision signature.
The committed manifest retains all previous cases and requires 173 workloads.

```sh
dotnet test XamlG.slnx -c Release
python3 scripts/compare-fast-paths.py --baseline-ref 1fcd01dc841d6551a38846990911e84fbea4ee2a --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py --baseline-ref 1fcd01dc841d6551a38846990911e84fbea4ee2a --iterations 3
python3 scripts/verify-compiler-performance.py \
  --baseline 1fcd01dc841d6551a38846990911e84fbea4ee2a --candidate "$(git rev-parse HEAD)" \
  --require-identical-sources --workload-manifest tools/XamlG.PerformanceProbe/workloads.json
```

All existing platform, MSBuild, upstream, original-theme, package, workspace, host,
Complete ControlCatalog, all-shard browser and compiler-evidence checks remain
mandatory. Generated code shape/runtime behavior must remain unchanged. Use the PR's
exact-head evidence for measured results, including residual costs and whole-Csc
wall/CPU behavior. The complexity fix does not establish XamlX parity or a whole-build
multiplier.
