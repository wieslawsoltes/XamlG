# Sparse decoded XML source maps

Baseline: `07d82abd273d907195231662810fe18071c0f197`.

`XamlDecodedTextMap` used one integer per decoded UTF-16 boundary whenever any XML normalization was necessary. One entity inside a large plain value therefore retained an O(N) boundary array and required O(N) individual boundary writes, in addition to the decoded string. Plain runs have a much simpler rule: raw position = decoded position + a constant offset.

The new representation keeps that constant in a checkpoint only when it changes. A supplementary numeric entity adds a forbidden checkpoint for its interior UTF-16 boundary followed immediately by its legal end. Literal surrogate halves remain ordinary boundaries. TAB, LF and lone-CR normalization may change the text without changing offsets and need no checkpoints.

## Representation and complexity

The map owns one private immutable array: either direct `int` boundaries or pairs of decoded position and offset. Null denotes identity offsets. This does not add another instance field or a static cache. No reference to the input source, callbacks, compiler context or mutable construction state is retained.

Values of at most 128 raw code units retain the original dense decoder and constant-time lookup. Larger values copy plain runs through the existing XML escape scanner. At publication the map chooses sparse pairs only when their actual payload is smaller than a direct boundary array. Otherwise it reconstructs direct boundaries. This final decision also handles dense changes occurring after a long plain prefix; it does not trust a prefix-density guess.

With N input units, D decoded units and B checkpoints, creation remains O(N). Sparse map construction/retention is O(B), excluding the decoded string and StringBuilder. Sparse range lookup is O(log B), versus O(1) for direct/identity maps. Dense reconstruction is O(D + B). Payload comparison uses widened arithmetic. No pooling, unsafe code or dependency is introduced. Generated code and runtime execution remain unchanged.

**Tradeoffs:** dense large input initially accumulates temporary checkpoints before choosing direct storage, which may allocate more and take longer than the old dense decoder. Sparse lookups pay binary search and representation dispatch. Short maps intentionally retain their prior boundary-allocation cost. These costs must be measured, not inferred away from the sparse memory benefit. Array payload counts exclude array/object headers, strings, temporary builders and overall retained process heap.

## Correctness coverage

The 24 C# cases compare every boundary against an independent per-character map, preserving source origins, entity length limits, invalid-entity exception parameters/messages, XML-only whitespace, UTF-16 and forbidden surrogate boundaries. Tests exercise short/sparse/dense/late-dense inputs, parallel immutable reads, nested markup source ranges and repeated execution of compiled standalone/shared factories. Every previous parser, tooling, compiler and runtime gate stays enabled.

`scripts/test-decoded-source-map-model.py` independently exercises the mapping invariant, strict checkpoint ordering, forbidden-boundary adjacency, exhaustive small token sequences and 1,000 seeded documents. Its execution does not replace running C# tests.

## Measurement

The probe retains all 275 previous cases and adds 50 workloads, pinning 325 names. Eight shapes at three scales each measure map creation and a batch of 32 precomputed legal point queries separately. Inputs, queries, signatures, reflection and payload observations are outside timing. Two complete encoded-markup binding fixtures must emit and compile successfully outside timing; their generated sources, mappings and diagnostics enter the signature.

Mapping representation counts appear separately under `decoded_map_storage`: array elements and `Marshal.SizeOf` element widths, not a GC-retained-heap measurement. Raw UTF-16 and every legal/illegal boundary enter the cross-revision signature. Both revisions execute the same probe source. Dense/unique/unchanged shapes remain present alongside sparse cases.

```sh
dotnet test XamlG.slnx -c Release
python3 scripts/test-decoded-source-map-model.py
python3 scripts/test-compiler-performance-gates.py
python3 scripts/compare-fast-paths.py --baseline-ref 07d82abd273d907195231662810fe18071c0f197 --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py --baseline-ref 07d82abd273d907195231662810fe18071c0f197 --iterations 3
```

Acceptance requires all exact-head native/MSBuild, host, workspace, package, upstream, original-theme, browser and complete-catalog workflows. The strict evidence verifier must pass all 325 cases, complete process/compiler pairs, exact identities, semantic signatures and 390 byte-identical generated catalog files. Record residual regressions and distinguish localized creation/query behavior from generator or full-build performance. No timing improvement is asserted before reviewing measured evidence.
