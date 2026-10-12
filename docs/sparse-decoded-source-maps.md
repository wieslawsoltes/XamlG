# Sparse and adaptive decoded XML source maps

Baseline: `07d82abd273d907195231662810fe18071c0f197`.

`XamlDecodedTextMap` previously wrote one integer per decoded UTF-16 boundary whenever any XML normalization was necessary. A single entity in a large plain value therefore required O(N) boundary writes and retained an O(N) array in addition to the decoded string. Plain runs instead obey raw position = decoded position + constant offset.

## Representation and ownership

The map owns one private immutable array: direct integer boundaries or pairs of decoded position and offset. Null means identity offsets. Checkpoints record only offset changes and forbidden boundaries inside supplementary numeric entities. Each forbidden midpoint is immediately followed by its legal entity end. Literal surrogate halves remain ordinary editable boundaries. TAB, LF and lone-CR normalization can change text without changing offsets.

There is no new object field or static cache, and no source text, compilation, callback, mutable builder or context is retained. Short values (at most 128 raw UTF-16 units) retain the original dense decoder. Large values initially copy ordinary runs and record sparse checkpoints.

## Adaptive creation

The first 325-workload checkpoint showed dense-BMP creation at scale 65,536 regressing from 186.542 to 343.743 microseconds while allocation grew from 341,280 to 472,336 bytes. That checkpoint was not accepted for merge merely because the exact-output checks passed.

After at least 32 checkpoints, the corrected implementation promotes once when a direct table already has no larger payload than the current sparse prefix. It reconstructs only the decoded prefix, then continues decoding directly without reparsing input. Alternating density cannot cause repeated prefix reconstruction: promotion is one-way during decoding.

An ordinary final tail contributes no checkpoints and is appended without constructing temporary boundary entries. At publication, even a promoted map can compact directly from temporary storage when a later plain tail makes sparse payload smaller. It never creates a final dense array just to throw it away. Final payload is the smaller of 8B and 4(D+1) bytes for large mapped input, excluding array headers and decoded text; B is checkpoint count and D decoded length. Dense reconstruction uses widened arithmetic.

Construction remains O(N) time. Temporary boundary storage is O(B) before promotion and up to O(D+B) after promotion; final sparse storage is O(B). A dense prefix followed by a long interior plain run and another entity can still require O(D) temporary boundaries even if the retained map is sparse. This cost is explicitly benchmarked.

Dense and identity queries are O(1). Sparse queries are O(log B), with O(1) affine paths before the first and after the last checkpoint. No probabilistic equality, pooling, unsafe code, new dependency or generated-runtime change is introduced.

## Correctness gates

The original 24 C# cases preserve every boundary against an independent per-character oracle: source origins, all numeric entity limits, malformed-entity exception contracts, XML whitespace, arbitrary UTF-16, dense/late-dense values, parallel readers and compiled standalone/shared factories.

Eleven additional C# cases cover density admission boundaries, dense-to-plain and alternating runs, exact final-tail offsets, sparse publication after dense construction and malformed input after promotion. Both text and attribute modes compare every legal/illegal range endpoint.

The six existing independent Python model tests remain. Two further tests in `scripts/test-adaptive-decoded-map-model.py` verify reconstruction/compaction and outer-region lookup over boundary cases and 2,000 seeded density transitions. These models do not replace actual C# execution.

## Measurement and acceptance

All 275 pre-map workloads and all original 50 map cases remain. Twenty-four density-transition cases bring the manifest to **349 workloads**. Twelve shapes at scales 32, 4,096 and 65,536 measure construction and 32 precomputed legal point queries separately. These scales are fixture parameters; reports retain actual raw and decoded lengths. New shapes cover dense prefixes, dense prefixes with a late entity, alternating density and dense CRLF.

Inputs, queries, reflection, signatures and fixture compilation are outside measurement. Two encoded-markup binding fixtures must emit and compile. Raw UTF-16 and every legal/illegal boundary enter the cross-revision signature. Mapping array element counts/widths are reported separately, not as retained-process-heap measurements. Both revisions compile and execute the same probe source.

```sh
dotnet test XamlG.slnx -c Release
python3 scripts/test-decoded-source-map-model.py
python3 scripts/test-adaptive-decoded-map-model.py
python3 scripts/test-compiler-performance-gates.py
python3 scripts/compare-fast-paths.py --baseline-ref 07d82abd273d907195231662810fe18071c0f197 --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py --baseline-ref 07d82abd273d907195231662810fe18071c0f197 --iterations 3
```

Merge requires all applicable exact-head native/MSBuild, host, workspace, package, upstream, original-theme, browser and complete-catalog gates. Require every pinned workload, complete process/compiler pairs, exact SDK/source identities, matching semantic signatures and 390 byte-identical catalog files. Dense setup, short inputs, interior sparse-query cost and whole-compiler regressions must remain visible. No corrected speedup is claimed before reviewing its measurements, and localized wins do not establish a universal build gain or XamlX parity.
