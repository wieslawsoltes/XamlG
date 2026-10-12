# Value-emission temporaries and array-rank formatting

Baseline: verified frame-analysis merge `f59ca5071a88800fcedbe7d09279e4a07f4d57c2`.

## Capture ownership and evaluation order

`ValueEmitter.Emit` previously supplied capturing delegates for the two converter payload variants. Those captures can require a compiler-generated display class even for ordinary literal/array/call branches. The converter helper now takes the existing immutable expression and dispatches the text/value payload itself. The payload is still emitted at the exact previous callback position, after the converter local and optional source information. No converter construction, source callback, provider, text conversion, nested payload evaluation, failure or runtime result is deduplicated.

`EmitInitialized` delegates to a separate private object-initialization path only for nonempty initializers on an object expression. Its Action remains occurrence-specific where necessary. Empty and invalid paths no longer need the initializer capture. Invalid input still reports the same diagnostic before lowering the value. Initializers, arguments, frame, source context and emitter are never retained outside the existing emission lifetime. No cache, pool, unsafe code, dependency or public API is added.

The allocation assertion deliberately uses already-constructed null/Boolean IR with warmed dispatch and no writer output. It checks the capture cost alone; strings and normal emitted C# still allocate their required results. Benchmarks use the same pre-existing methods bound to delegates outside timing in both revisions. Reflection setup and test input allocation are not attributed to lowering.

## Linear array suffix construction

The previous formatter concatenated every nested rank onto a growing suffix. For R fixed-size nested ranks it copied O(R²) characters. Zero/one nested ranks retain direct string paths; deeper shapes use one query-local StringBuilder and append each bracket/comma once. Work becomes amortized O(R + C) for C suffix characters, with O(C) temporary/final text. It does not change underlying C# type-name formatting, rank ordering, native count formatting, array allocation semantics or element evaluation. No attempt is made to alter unrelated unsupported outer-array shapes.

The builder has a setup cost for shallow two-rank shapes. The manifest includes zero, one, two, 32, 256 and 1,024 ranks, both jagged and mixed-rank. Synthetic deep type formatting is not represented as execution of a deeply nested runtime application. Separate shallow mixed-rank factory tests actually compile the emitted expression and return type.

## Quality and measurement

Twenty-two C# cases cover exact-zero ordinary capture paths, cancellation and invalid initializers, eight standalone/shared nested-converter scenarios, each runtime failure boundary and source-info callback, independent repeated builds, frozen rank output, actual generated C# compilation, and parallel formatting. Existing collection/deferred/argument initialization, source mapping, trimming and complete runtime tests remain enabled.

Twenty-two new probe workloads retain all 416 predecessors (438 total). Six cases isolate ordinary null/Boolean/escaped-string emission with and without empty initializers. Twelve cases exercise array-rank scaling. Four complete 32/512-view converter fixtures bind outside timing, emit repeatedly with fresh contexts, and must compile outside timing; source, mappings and diagnostics enter the cross-revision signature. These are emission timings, not binding, runtime construction, fresh-process startup or complete builds.

Use the normal native/MSBuild Linux/Windows/macOS, all-shard browser, workspace, upstream, original themes, clean package consumers, host and complete headless/desktop/trimmed-WebAssembly catalog gates. Run `compare-fast-paths.py --baseline-ref <baseline> --pairs 3`, the normal-analyzer full/captured-C# catalog comparison, and `verify-compiler-performance.py` with the committed manifest and `--require-identical-sources`. Both revisions execute identical probe source. Require all 390 catalog-generated files byte-identical and exact semantic signatures. Local Python/model/source verification is not C# execution.

Report shallow/deep/cold/setup/whole-Csc CPU and wall tradeoffs, not only the largest synthetic improvement. Allocation counters are not retained-process-heap measurements. No universal build gain or XamlX parity follows from these changes.

## Retained correction checkpoint

The first array formatter commit accidentally changed the deferred-customizer declaration during full-file transfer. The original-theme gate rejected its generated `object` local where IDeferredContent was required. The following correction restores `deferred.TargetType.CSharpName()` and the complete reviewed local tree. The failure remains in CI history; neither its source checks nor existing assertions/timeouts were weakened. Final acceptance must use the corrected exact head.
