# Measuring generator performance

Performance optimization and speed targets were canceled on October 9, 2026. These tools and historical results remain available for optional diagnostics. Compiler correctness, integration and runtime validation are the final acceptance criteria; no timing threshold is a merge gate.

`tools/XamlG.Benchmarks` is an optional console harness using the real incremental generator, Roslyn compilation and Avalonia framework profile. It is outside the shipping package inventory and default solution. Run it with the pinned SDK:

```sh
dotnet build tools/XamlG.Benchmarks -c Release -warnaserror
dotnet tools/XamlG.Benchmarks/bin/Release/net10.0/XamlG.Benchmarks.dll 9 32 24 > measurements.json
```

The positional arguments are sample count, document count and controls per document. The default workload contains 32 Avalonia documents with 24 `TextBlock` children each. Every child uses a duck-typed markup provider. The harness first emits the complete generated application successfully, then verifies source count and generator diagnostics after every timed operation. Application assembly emission is excluded from the timings.

Scenarios cover a fresh Roslyn compilation and driver, a fresh driver over an existing compilation, an unchanged incremental run, an edit to one XAML document, and an unrelated C# edit. A separate microbenchmark performs 10,000 provider selections across three framework control types and one provider type. Each scenario has three warm-up operations followed by nine measured samples by default. The JSON reports median/minimum/maximum elapsed time and median allocations across all process threads, including concurrent document workers. Historical tables below used calling-thread allocations and should not be compared directly with the new allocation totals. Garbage collection runs before each sample, outside the timed interval. Metadata references and source text are already available; this does not measure process startup, disk I/O, NuGet restore, MSBuild or runtime construction.

For the complete ControlCatalog and Simple/Fluent theme comparison against XamlX, use [the project compilation benchmark](controlcatalog.md#compilation-benchmark). Its measurements include the cost of compiling generated C#; the microbenchmark in this document excludes that cost.

The [direct C# optimization research](source-generation-optimization.md) records the XamlX/Roslyn structural comparison, current transformation audit and remaining runtime parser inventory.

## Profiling the complete compiler

`scripts/profile-controlcatalog.py` captures the real Csc invocation for Simple, Fluent and ControlCatalog, runs it in fresh processes with every generator and analyzer retained, and then collects a separate EventPipe trace. Prepare the pinned sources with `scripts/prepare-controlcatalog.py` first. Install `dotnet-trace` 9 or later; the validated version is 9.0.661903.

```sh
dotnet tool install dotnet-trace --tool-path artifacts/profile-tools --version 9.0.661903
python3 scripts/profile-controlcatalog.py --dotnet-trace artifacts/profile-tools/dotnet-trace
dotnet build tools/XamlG.TraceAnalysis -c Release -warnaserror
dotnet tools/XamlG.TraceAnalysis/bin/Release/net10.0/XamlG.TraceAnalysis.dll \
  artifacts/controlcatalog-profile/Avalonia.Themes.Simple/compiler.nettrace
```

Use `--dotnet`, `--projects`, `--iterations` and `--output` to select the SDK, workload and an empty output directory. The default is three unprofiled Csc runs per project. `--skip-prepare` requires already restored and built dependencies for the current revision. The tool retains the actual assembly name, response file, generated C#, analyzer reports, process CPU/wall measurements, managed stack summaries, Speedscope profiles and allocation/GC traces. The main compiler measurements and traces retain every analyzer. It records any tracked changes; use a clean committed revision for published comparisons.

`--isolate-phases` also runs controlled diagnostic comparisons in rotating order: the full compiler, the same captured C# replacing the XamlG generator, that captured C# with ILLink analysis removed (when present), and captured C# with analyzers skipped. Other source generators remain enabled. Each uses the same assembly identity, references and compilation options. These variants identify expensive stages and interactions; they are **not acceptance benchmarks** and do not change production builds. Loading generated C# as input syntax trees can change Roslyn's work, so subtracting these timings does not give exact additive phase costs. `phases.json` records all measurements and the source hashes.

`timings.md` reports XamlG generation time, all-generator time, complete Csc time and the separate captured-C# compilation with its normal analyzers. Generation times come from Roslyn's `/reportanalyzer` output in the actual full compiler runs; the JSON retains them for each measurement. The captured-C# measurement includes parsing, binding, analysis, emission and compiler startup. Use these measurements to optimize generation and the emitted code independently, then verify the complete pipeline. Analyzer times overlap compilation and must not be subtracted from Csc wall time.

The trace reader also resolves allocation-event call stacks. It reports inclusive allocating frames, the nearest generator/analyzer/Roslyn owner, and the nearest XamlG frame. This distinguishes allocations made by the generator from allocations in C# compilation and analysis. Inclusive frames overlap; a nearest owner is an attribution rule, not an exact phase boundary. The temporary ETLX index is deleted after reading. The manually dispatched profiling workflow publishes these diagnostics for all three projects.

Both the profile and XamlX benchmark pin compiler subprocesses to the selected installation with `DOTNET_ROOT`, its host-architecture override and `DOTNET_HOST_PATH`. The profiler launches the captured compiler executable. This matters for an SDK installed outside the system location: its native Csc apphost can otherwise load a different globally installed runtime even when MSBuild itself uses the requested SDK. Earlier local profiles explicitly launched `csc.dll` through the selected dotnet, while local MSBuild rebuilds could select another runtime; those timings should not be compared as equivalent compiler processes.

The sampled managed thread-time profile includes waits and GC, sums concurrent threads and has overlapping inclusive frames. It is not an on-CPU profile. Allocation ticks estimate allocated bytes and attribute each interval to the sampled type; they do not count every allocation. Trace timings include profiling overhead. The unprofiled Csc runs include generation, generated C# compilation and analysis, but exclude MSBuild. Use the separate XamlX benchmark for the added-XAML-cost comparison and report full rebuild time alongside it.

## Comparing compiler revisions on one runner

Use `scripts/compare-controlcatalog-compilers.py` to measure a compiler change against a selected commit on identical project inputs:

```sh
python3 scripts/prepare-controlcatalog.py
DOTNET_PROCESSOR_COUNT=2 python3 scripts/compare-controlcatalog-compilers.py \
  --baseline-ref <baseline-commit> --iterations 6
```

The baseline commit must be available locally. The script builds its generator in a temporary detached worktree, freezes both generators and their dependencies, and removes the temporary checkout. Both variants consume the current sample sources, runtime and assembly references. This isolates compiler changes; it does not compare different runtime implementations or sample revisions. An older compiler must support the current project inputs and runtime contracts.

Each project runs adjacent before/after pairs for the full Csc invocation and for captured generated C#, reversing order between iterations. Builds, restores and source captures happen outside the measured runs. The default six pairs balance order; at least three are required. Use `--dotnet`, `--projects` and an empty `--output` directory to select the SDK and workloads. `--skip-prepare` requires current dependencies already restored and built. Run without competing builds or profilers on the same machine.

`results.json` records commit identities, frozen compiler hashes, generated-source hashes and sizes, every process measurement, phase medians and individual paired ratios. `summary.md` reports generation, full Csc and captured-C# timings separately. The full phase retains every generator and analyzer. The captured phase replaces the XamlG generator/analyzer assembly with its generated sources and retains the other generators and analyzers. Its timing is diagnostic and cannot be subtracted from full Csc to derive an exact generation cost. The comparison does not measure added XAML cost against XamlX; use `benchmark-controlcatalog.py` for that diagnostic comparison.

The **Compiler profiling** workflow accepts an optional `baseline_ref` and a `pairs` count for this comparison. A manual run with a baseline uses one runner for all pairs and uploads the `compiler-comparison` artifact. Without a baseline, the workflow retains its normal profiling behavior. Published comparisons should use clean committed revisions; local runs also record tracked changes and the harness hash.

Source-size, IL-size and isolated generator improvements do not establish a compilation improvement. Retain performance-only changes when paired full-Csc measurements with normal analyzers demonstrate a benefit and runtime validation supports the tradeoff. Inconclusive changes remain experiments; do not present them as progress toward parity. Keep correctness, parser coverage and demonstrated scaling fixes distinct from workload-speed claims. Compare revisions with equivalent functionality, and report the full XamlX added-cost comparison separately. Historical speed targets below are no longer active.

## Compilation-scoped metadata caching

The compatibility review found repeated scans for provider methods and declared/inherited content properties, including negative lookups for ordinary controls. The type system now retains these immutable results within one Roslyn compilation and framework configuration. Generator environment creation also no longer constructs an unused second type system. Changing the C# compilation creates a new environment; the generator regression checks provider removal and restoration after cached positive and negative results.

The initial comparison used macOS 26.6 on Arm64, .NET 10.0.12, SDK 10.0.401, nine samples and the default 32 × 24 workload. The baseline compiler was `c964d82`; local before/after JSON and validation logs are retained under `artifacts/tests/compatibility-review/performance/`.

| Scenario | Median before | Median after | Allocated bytes before | Allocated bytes after |
| --- | ---: | ---: | ---: | ---: |
| Fresh compilation and driver | 258.9 ms | 196.6 ms | 254,546,080 | 226,726,560 |
| Fresh driver | 237.5 ms | 160.3 ms | 254,349,296 | 225,532,144 |
| Unchanged run | 0.127 ms | 0.095 ms | 19,080 | 19,080 |
| One XAML edit | 16.4 ms | 9.6 ms | 9,859,256 | 8,982,200 |
| Unrelated C# edit | 216.8 ms | 156.6 ms | 253,154,968 | 224,338,040 |
| 10,000 provider selections | 89.3 ms | 0.125 ms | 50,980,000 | 0 |

The full-generation scenarios improved by roughly 24–32% in this local measurement and allocated roughly 11% less memory. Microbenchmark gains are not application speedups; especially the sub-millisecond unchanged-run timing is sensitive to noise. Re-run the harness on the intended project and machine before drawing broader conclusions.

The cache change passed all 252 core tests, all 1,258 Avalonia tests, and all 452 portable compatibility/shared parity tests with no skips. The benchmark and Avalonia test projects built with warnings treated as errors. These checks include inherited metadata, provider selection, generator edits, implicit collection contracts and runtime construction; broader final review gates remain tracked separately.

## Final implementation comparison

The full compatibility revision `8ebd279` was compared again with baseline `c964d82`, using identical harness source, three warmups and nine samples per scenario. The baseline and reviewed compiler ran serially, with local integration jobs stopped during measurement. Compiler identities, harness hashes and raw results are retained under `artifacts/tests/8ebd279/`.

| Scenario | Baseline median | Reviewed median | Baseline allocated bytes | Reviewed allocated bytes |
| --- | ---: | ---: | ---: | ---: |
| Fresh compilation and driver | 237.4 ms | 214.3 ms | 255,543,456 | 228,423,520 |
| Fresh driver | 200.0 ms | 178.9 ms | 254,346,224 | 227,220,960 |
| Unchanged run | 0.111 ms | 0.090 ms | 19,080 | 19,080 |
| One XAML edit | 15.4 ms | 11.6 ms | 9,859,160 | 9,067,640 |
| Unrelated C# edit | 214.1 ms | 180.0 ms | 253,152,920 | 226,026,840 |
| 10,000 provider selections | 88.1 ms | 0.122 ms | 50,980,000 | 0 |

The final full-generation scenarios were about 10–16% faster with 10.6–10.7% fewer allocated bytes; the one-document edit was about 25% faster. Timing varies with machine load: an earlier follow-up during integration work was slower and remains in `artifacts/tests/compatibility-review/performance/final.json`. Allocation reductions persisted. These are local generator measurements, not application startup or runtime speed guarantees.
