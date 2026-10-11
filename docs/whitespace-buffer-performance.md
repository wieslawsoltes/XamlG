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
Time remains O(N), including bounded lookahead at the scalar transition.
This is a constant-factor/allocation pass following #36's algorithmic improvement,
not a new asymptotic class or generated-runtime change.

## Storage ownership

Up to 256 UTF-16 characters use stack storage (at most 512 payload bytes). Larger
inputs up to 16,384 characters request a shared ArrayPool rental. The initialized
prefix is cleared in `finally` before return so this operation does not retain
its source text in a process-wide pool. Output is an independent immutable string.
Above that request threshold sparse values use a normal unpooled array and dense
values retain the previous StringBuilder suffix. Neither branch adds large pool
buckets through this path. No user callbacks,
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

Twenty-one C# cases cover all trim modes at lengths 0/1/255/256/257/4096/16383/16384/
16385/131073, dense/sparse/late changes, exhaustive short spellings, canonical
reference identity, contaminated shared-pool storage, parallel calls and UTF-16.
Expected results come from a frozen original pending-space implementation.
Existing normalization, binding, source mapping, generated execution and callback
oracles remain mandatory.

Twenty-four new probes cover dense/late/boundary changed input at the stack and
rental boundaries and at 64/128 Ki-character lengths. Raw UTF-16 signatures cover
all four trim modes without encoding unmatched surrogates through UTF-8. The
manifest retains every prior case and requires 275 workloads. Both revisions
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

## Reviewed first checkpoint and adaptive correction

The first 267-workload checkpoint (`d4a70d1`, tested checkout `b8f346e`) passed
semantic/native/source gates but is not the final candidate. Dense changed text at
4,096 characters used 11,928 -> 3,664 allocated bytes and 14.482 -> 13.374 us.
However, the 4,096-character boundary-only case rose 10.634 -> 12.900 us, and the
131,073-character dense case rose 408.708 -> 507.051 us while saving only 48 bytes.
Those regressions reproduce in all three process-pair medians and are not dismissed
as noise. Original measurements remain in CI artifact 11715500162.

The correction checks at most 16 characters at the current post-whitespace boundary.
A long non-whitespace run uses the existing span IndexOfAny primitive and one bulk
copy, rather than one checked destination store per character. After each such run,
whitespace is skipped and the bounded test is repeated. At the first short run the
writer falls back once to the scalar pending-space loop for the remaining suffix.
No repeated growing-prefix copying, resizable output or unbounded backtracking is
introduced. All scanned/copy runs remain disjoint except for bounded lookahead at
the scalar transition. Pending-space state, start/end policies and output capacity
are unchanged. No Unsafe, pointers, new dependency or generated-code change is used.

Above the rental cap, a non-long initial run uses the exact previous StringBuilder
suffix. This deliberately gives up its negligible 48-byte object saving instead of
keeping the measured large-dense regression. Sparse large values retain the bulk
span path. The extra dispatch/probe and mixed-density suffixes can still have costs;
this is not a universal no-regression guarantee.

Seven additional C# cases cover dense/long transitions, both buffer boundaries,
all trim modes and run lengths 15/16/17. Eight added probes cover both transition
directions at 257/4,096/16,385/65,536 characters. Every original case remains, and
all transition outputs enter raw UTF-16 signatures. The revised candidate requires
new exact-head CI/performance review; earlier passing tests do not validate it.
