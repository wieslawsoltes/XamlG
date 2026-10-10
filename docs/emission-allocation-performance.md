# Emission allocation and wide-shape pass

Baseline: `4599636974aaf37d547e136d4161dfe775df1059`. This follows the runtime-contract, loader and incremental resource work in PR #26. It targets work still done inside C# emission, with a byte-identical generated-source policy rather than a new lowering strategy.

## Changes

**String literals.** Printable ASCII without a double quote or backslash can be copied directly between quotes. Escapes, control characters, non-ASCII characters and malformed UTF-16 still use Roslyn's formatter. No process-wide string cache is introduced. Differential tests compare every UTF-16 code unit, mixed strings, surrogate sequences and long literals against the exact Roslyn formatter. This avoids inventing a second Unicode escaping grammar. A late escape can cause an extra preliminary scan before fallback; that cost is deliberately measured.

**Shared helper parameters.** Looking up each of P literal parameters by scanning a P-element array made wide shared bodies quadratic in P. Small sets retain a bounded linear scan (at most eight entries). Larger sets create a reference-identity index only when the helper body actually requests substitution, not while collecting every matching occurrence. Indexing and all substitutions are expected O(P). Duplicate references retain their first slot; value-equal but distinct bound expressions never alias. The arrays are emitter-owned and not mutated after planning. The internal holder is a class, so record cloning cannot accidentally carry a cached index onto different parameter arrays. Cached names avoid reformatting wide parameter ordinals. No symbols or bound nodes enter a global strong cache.

**Leaf and deferred calls.** Repeated `call += fragment` copied the accumulated prefix once per scalar property, creating quadratic copying for wide calls. Calls now use append-only builders, then one final string. Literal order, line directives, exact whitespace, generated offsets and source mappings are unchanged. Tests execute standalone and multi-document wide leaves/markup with directives both enabled and disabled.

**Immediate expression edges.** Lifetime-hazard and resource walks previously created singleton arrays, copied immutable argument arrays to insert receivers, and layered concatenation iterators. Common scalar, call, indexer, array and assignment shapes now use an unboxed value-type sequence over existing immutable arrays with optional leading/trailing edges. Compound choice, initializer and builder edges use a single remainder iterator instead of arrays for each edge. Object edge enumeration remains an iterator. This is not a claim that every whole-tree traversal allocates nothing.

The immediate walker preserves duplicate occurrences, constructor/receiver/descriptor order, post-call order, choice fallback order and the include-deferred boundary. Early termination disposes an already-started remainder and does not start an untouched remainder. Existing explicit-stack whole-object traversal and its deep-tree contracts are unchanged. Tests enumerate every existing composite and assignment kind, repeat enumeration, interleave enumerators and check direct common-call enumeration allocation.

## Validation and measurements

Run the ordinary native solution and existing platform/browser/corpus gates, then:

```sh
python3 scripts/compare-fast-paths.py --baseline-ref 4599636974aaf37d547e136d4161dfe775df1059 --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py --baseline-ref 4599636974aaf37d547e136d4161dfe775df1059 --iterations 3
python3 scripts/verify-compiler-performance.py --baseline 4599636974aaf37d547e136d4161dfe775df1059 --candidate "$(git rev-parse HEAD)" --require-identical-sources
```

The existing 35 workloads remain. Ten additional measurements cover short/long ASCII literals, Unicode/escaped literals, late escaping and emission of eight repeated leaf or markup occurrences at widths 8, 64 and 256. The same probe is compiled independently against both revisions. Input construction, reflection/delegate setup, binding and correctness snapshots are outside the added emission timings. All output sources and mappings from wide fixtures contribute to the cross-process semantic signature.

These probes diagnose isolated work; they are not equivalent to incremental build time or XamlX compile cost. Full-Csc and captured-C# reports retain normal analyzers and remain independent measurements. Timings must be read together with allocations and generated-source/semantic equivalence. Tests, mappings, runtime semantics, trimming analysis and browser acceptance are not disabled to achieve an improvement. Actual final-head measurements and any regressions belong in the PR evidence, not inferred from complexity alone.
