# Bounded source-fingerprint scratch

Baseline: `ee78d5c72a409e7c50413b9b85973d8fe8c8ce02`. This pass changes UTF-8 input buffering, not the SHA-256 digest or the 96-bit lowercase identifier format. Source text, source spans, type prefixes, generated helpers and runtime operations remain unchanged.

## Algorithm and ownership

The former emission-local byte array grew to exactly every larger requested capacity and retained the largest capacity. For k slightly increasing requests up to N characters, cumulative scratch allocations could be O(kN), quadratic for unit increments. Input encoding and hashing still require O(sum of requested lengths): this pass does not claim to eliminate repeated subtree hashing.

`SourceFingerprint` keeps the first small allocation exact, grows geometrically thereafter, and caps the buffer at 16 KiB. Old contents are scratch and are not copied when capacity grows. Below the cap, total buffer allocation is bounded by a geometric series; a clipped final growth can add at most one capped array. Above the cap, large inputs are encoded and hashed in chunks using the same buffer. The backing byte array never grows past the cap, and context disposal releases its reference. The provider, returned digests, original strings, generated output and other context fields remain separate memory costs.

Small inputs retain `ComputeHash`. Large inputs use the existing SHA-256 provider with `TransformBlock` and `TransformFinalBlock`, followed by `Initialize` on success or failure. Internal chunks never split a valid UTF-16 surrogate pair. The caller's slice boundaries are authoritative; a pair split there must retain the original replacement fallback. Prefix and value are independently encoded, separated by the same single NUL byte even for an empty (but non-null) prefix. Null and empty prefixes are not interchangeable.

No pool, global cache, new per-context helper object, decoder, persistent char array, source substring or cached hash result is introduced. The reusable scratch belongs only to one emission context; independent contexts may execute concurrently, while concurrent use of one emission context was and remains unsupported.

## Measurements and tradeoffs

The manifest retains all 482 predecessor workloads and adds 30: 12 warm hashes (ASCII/Unicode and 32/4096/5460/5461/65536/1048576 characters), eight fresh-context cases, four increasing/decreasing sequences, two large-prefix cases and four full-emission fixtures. Fresh-context measurements include construction, initial identity hashing and disposal, but not reflection/delegate compilation or input generation. Successful full fixtures are compiled outside timing. Each process records exact `_hashInput` array payload capacity before and after disposal; this is not a process-heap or working-set measurement.

The benchmark implementation uses only the pre-existing `EmissionContext` entry point, so exactly the same probe source compiles and runs against both revisions. Frozen byte assembly checks every tested direct digest, and complete emitted source/mappings/diagnostics contribute to the semantic signature. The three existing catalog projects continue to require 390 byte-identical generated files with normal analyzers enabled.

Review short-hash dispatch costs, geometric overcapacity, warm streaming CPU and provider overhead, first-use allocation and full-emission results before accepting the pass. A 16 KiB bound does not establish faster warm hashing or reduced retained memory for every small context. No timing, allocation or retained-heap improvement is claimed without actual results.

## Correctness coverage

Native tests cover all UTF-16 code units, 200 seeded slices, chunk transitions, paired/unpaired surrogates, prefixes that end in high surrogates, 1 MiB text, geometric growth/retention, large/small/empty transitions, invalid-span recovery, injected provider failure and independent concurrent contexts. The previous UTF-8 input assembler and independent digest spelling form the expected result; they do not call the candidate's chunking or hex helper.

```sh
python3 scripts/compare-fast-paths.py --baseline-ref ee78d5c72a409e7c50413b9b85973d8fe8c8ce02 --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py --baseline-ref ee78d5c72a409e7c50413b9b85973d8fe8c8ce02 --iterations 3
python3 scripts/test-compiler-performance-gates.py
python3 scripts/verify-compiler-performance.py --baseline ee78d5c72a409e7c50413b9b85973d8fe8c8ce02 --candidate "$(git rev-parse HEAD)" --require-identical-sources --workload-manifest tools/XamlG.PerformanceProbe/workloads.json
```

All normal native/MSBuild, browser, catalog, workspace, package, upstream, original-theme and host acceptance gates remain enabled. Local execution was unavailable while this pass was authored; actual C# execution and benchmark acceptance must come from repository CI. The PR conversation records final measured costs and benefits, rather than replacing them with algorithmic predictions.
