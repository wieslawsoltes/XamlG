# Changed-whitespace buffer optimization

Baseline: `d6d1391198d54efa034290a00171d2ca539ffa46` (merged normalization PR #36).

## Scope and invariant

Keep copy-on-change discovery exactly as it was. Canonical input returns the original
string, and blank input uses the existing trim-policy result. Only the changed suffix
writer changes: direct `Span<char>` stores replace the per-character StringBuilder
calls. The unchanged prefix is copied once; the already-scanned first changed run
is not scanned again. The original pending-space state machine still determines
all four trim policies. Only XML's four whitespace characters collapse; arbitrary
UTF-16 code units, including unmatched surrogates and NUL, remain unchanged.

A nonempty whitespace run emits at most one character, and every non-whitespace
character emits exactly itself. Therefore output length is at most input length;
the fixed-capacity writer does not grow, truncate, retry or change diagnostics.
Time remains O(N), with at most one prefix-discovery scan and one suffix scan.
This is a constant-factor/allocation pass following #36's algorithmic improvement,
not a new asymptotic class or generated-runtime change.

## Storage ownership

Up to 256 UTF-16 characters use stack storage (at most 512 payload bytes). Larger
inputs up to 16,384 characters request a shared ArrayPool rental. The initialized
prefix is cleared in `finally` before return so this operation does not retain
its source text in a process-wide pool. Output is an independent immutable string.
Above that request threshold the buffer is a normal unpooled array: exceptional
large values do not add large pool buckets through this path. No user callbacks,
source context, mutable syntax or pooled memory escapes the call.

The pool may allocate on a miss and retains arrays according to its own policy.
The request cap is not a global memory quota or a claim about every runtime's
actual bucket sizes. Returning rentals requires a clearing pass; this cost is
included in every timing. Stack use, zeroing, rent/return and above-cap allocations
can cost more on some inputs. Warm allocated-byte savings are not proof of lower
whole-process retained memory. Large unpooled arrays and the final output still
allocate. Existing XML entity decoding uses the same platform ArrayPool; this
pass does not change its implementation or ownership behavior.

## Quality and measurements

Fourteen C# cases cover all trim modes at lengths 0/1/255/256/257/4096/16383/16384/
16385/131073, dense/sparse/late changes, exhaustive short spellings, canonical
reference identity, contaminated shared-pool storage, parallel calls and UTF-16.
Expected results come from a frozen original pending-space implementation.
Existing normalization, binding, source mapping, generated execution and callback
oracles remain mandatory.

Twenty-four new probes cover dense/late/boundary changed input at the stack and
rental boundaries and at 64/128 Ki-character lengths. Raw UTF-16 signatures cover
all four trim modes without encoding unmatched surrogates through UTF-8. The
manifest retains every prior case and requires 267 workloads. Both revisions
compile and execute identical probe sources. Input creation/signature work is
untimed; the shared harness performs three alternating fresh-process pairs with
three warmups and five batches. Measurements describe warmed pool behavior, not
guaranteed cold rents. Existing 32/4096-character and complete-document benchmarks
still expose regressions rather than being replaced by favorable boundary cases.

The full-catalog harness retains normal analyzers and all three complete project
comparisons. Exact semantic signatures, SDK/source/harness identity and 390
byte-identical generated sources remain acceptance criteria. Every native/MSBuild,
workspace, browser, catalog, package, upstream, original-theme and host gate must
pass. No measured speedup is claimed until final-head artifacts are reviewed.

```sh
python scripts/test-compiler-performance-gates.py
python scripts/compare-fast-paths.py --baseline-ref d6d1391198d54efa034290a00171d2ca539ffa46 --pairs 3
python scripts/prepare-controlcatalog.py
python scripts/compare-controlcatalog-compilers.py --baseline-ref d6d1391198d54efa034290a00171d2ca539ffa46 --iterations 3
```
