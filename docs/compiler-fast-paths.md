# Compiler allocation and complexity pass — 2026-10-10

This pass targets compiler work and generated-document metadata initialization without removing diagnostics, source mappings, live-edit registrations, namescope boundaries, trimming analysis, or framework initialization semantics. The compiler and runtime remain compatible with their existing `netstandard2.0` targets.

## Algorithms and ownership

| Path | Previous work | New work |
| --- | --- | --- |
| Named-field initialization | Every emitted object scans all generated fields: O(N × F) | An emission-local reference-identity index is built in O(F); lookups and field emission take expected O(N + F). A dense link array and value-type enumerator avoid per-object lists and iterator allocations. |
| Dense metadata/accessor publication | Sort entries by indexes already assigned during registration: O(K log K) | Scatter into the assigned slots and traverse once: O(K). Slot ordering and source identities do not change. |
| Shared property discovery | Ordered-tree lookup and update for every repeated assignment | Hash-based accumulation, then ordinal sorting of unique shared owner/property keys once. Repeated assignment classification is also performed once rather than twice. |
| XML duplicate attributes | Allocate a hash table for every element | Scan at most eight existing attributes, then promote to a hash table. The bounded small case does not introduce quadratic growth for large attribute lists. |
| XML names | Allocate each repeated element, closing-tag and attribute name | A bounded parse-local atom table compares source slices. Name hashing is performed during the name scan. Names beyond 128 UTF-16 units and names beyond the 512-entry retention budget remain supported. |
| Markup source mapping | Rebuild a decoded source map and remap records even for identity mappings | Parse directly at the raw offset when the source slice equals the supplied text. Entity/normalization mappings retain the existing exact-source path. |
| Generated metadata initialization | Allocate numeric substrings and copy the decoder's new declaration dictionary | Bounded integer scanning for ordinary generated fields; transfer the decoder-owned dictionary into a read-only view. Public constructors still defensively copy caller-owned dictionaries. |

The XML, markup-extension and type-name grammars share lexical primitives where their rules match. XML whitespace is intentionally distinct from CLR/XAML literal whitespace. Balanced delimiters, quoted escapes, XML entities, error recovery and type-name grammar remain specialized rather than being forced into an incompatible universal splitter.

The XML atom table is not `string.Intern`: it is bounded and dies with its parser. The named-field index uses `ReferenceEquals` and `RuntimeHelpers.GetHashCode`, not the recursive equality/hash implementations of bound records. Dictionary traversal order is never used to assign shared helper slots.

Metadata integer scanning preserves `NumberStyles.None` for record framing and `NumberStyles.AllowLeadingSign` for fields. An uncommon compatibility fallback preserves the historical BCL treatment of externally supplied spellings such as trailing NUL characters. Generated decimal fields do not take that fallback. Overflow checks, record boundaries, null identity sentinels, duplicate declaration rejection and atomic lazy publication remain in place.

## Validation and reproduction

Run the ordinary solution tests and the differential comparison from a clean checkout with the repository SDK:

```sh
dotnet test XamlG.slnx -c Release
python3 scripts/compare-fast-paths.py \
  --baseline-ref f1536cae668df0ece3329ed883b3fac8bde260ea --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py \
  --baseline-ref f1536cae668df0ece3329ed883b3fac8bde260ea --iterations 3
```

The fast-path harness is compiled independently against both revisions, with exactly the same probe sources. It measures repeated-name and unique-name XML at 100, 1,000 and 10,000 children; 1,000-attribute XML; identity/entity markup; framing and decoding 1,000 metadata records; and emission of views containing 100, 1,000 and 5,000 named objects. Binding is outside the named-field emission timings.

Three alternating AB/BA process pairs each contain three untimed warmups and five measured batches per case. Tiered compilation is disabled for both microbenchmark variants. Input construction, build/restore, process startup and correctness snapshots are excluded. Allocation counts are thread-local allocated bytes. Raw samples, source identities and the measurement method are retained in the report; no timing threshold is imposed on shared CI machines.

The harness hashes typed syntax nodes, diagnostics, markup source ranges, decoded metadata, generated named-view sources and source mappings. It includes 500 deterministic malformed-input mutations. A mismatch fails the comparison by default; `--allow-semantic-changes` is reserved for separately reviewed intentional semantic changes, not performance regressions.

The existing full-catalog harness separately captures both generated-source trees, compares compiler revisions on identical project/runtime references, and reports generation time, full Csc cost and captured-C# compilation/analysis cost. These phases are independent and must not be subtracted or summed to manufacture an XamlX comparison. The CI job publishes the per-project generated-source hash-map equality alongside the timing report.

## Interpreting results

This is not a claim of XamlX performance parity. Localized parsing or metadata improvements do not establish the same improvement in total builds. This pass intentionally preserves the generated C# shape; its generated-runtime benefit comes from faster metadata initialization, not from disabling runtime functionality. Future emitted-code-shape changes require separate generated-code compilation, runtime, trimming and full-catalog validation.

Measurements belong to a specific commit, SDK and runner. Use the PR's `compiler-performance-comparison` artifact and job summary for actual observed results; do not infer speedup from the algorithm table alone.
