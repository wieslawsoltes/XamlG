# Measuring generator performance

`tools/XamlG.Benchmarks` is an optional console harness using the real incremental generator, Roslyn compilation and Avalonia framework profile. It is outside the shipping package inventory and default solution. Run it with the pinned SDK:

```sh
dotnet build tools/XamlG.Benchmarks -c Release -warnaserror
dotnet tools/XamlG.Benchmarks/bin/Release/net10.0/XamlG.Benchmarks.dll 9 32 24 > measurements.json
```

The positional arguments are sample count, document count and controls per document. The default workload contains 32 Avalonia documents with 24 `TextBlock` children each. Every child uses a duck-typed markup provider. The harness first emits the complete generated application successfully, then verifies source count and generator diagnostics after every timed operation. Application assembly emission is excluded from the timings.

Scenarios cover a fresh Roslyn compilation and driver, a fresh driver over an existing compilation, an unchanged incremental run, an edit to one XAML document, and an unrelated C# edit. A separate microbenchmark performs 10,000 provider selections across three framework control types and one provider type. Each scenario has three warm-up operations followed by nine measured samples by default. The JSON reports median/minimum/maximum elapsed time and median allocations across all process threads, including concurrent document workers. Historical tables below used calling-thread allocations and should not be compared directly with the new allocation totals. Garbage collection runs before each sample, outside the timed interval. Metadata references and source text are already available; this does not measure process startup, disk I/O, NuGet restore, MSBuild or runtime construction.

For the complete ControlCatalog and Simple/Fluent theme comparison against XamlX, use [the project compilation benchmark](controlcatalog.md#compilation-benchmark). Its target includes the cost of compiling generated C#; the microbenchmark in this document excludes that cost.

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
