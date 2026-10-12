# Emission bookkeeping: direct mapping and context-owned spellings

Baseline: `04f87b2be025ed7d257723504ad508fd6aa3aba3` (merged cast/mapping pass).

## Scope and invariants

Object assignment lowering brackets its existing direct call with `BeginMap` and `EndMap`. This removes the five-reference value tuple and generic static delegate trampoline; it does not reintroduce a closure. The two callback-based mapping overloads remain and use exactly the same entry/completion functions. Completion is deliberately not in `finally` or `Dispose`: exceptions retain partial output without publishing a completed mapping or trailing `#line default`. Suppression is captured at entry, not re-read after nested lowering. Cancellation remains before every assignment callback/lowering.

The immutable document and stable Id own four lazy strings: local service type, source table name, source setter name and escaped path. Shared-service selection is mutable and is checked before the local fallback on every getter invocation. Replacing shared services, reverting to local services or emitting a different document cannot observe another selection's cache. No static registry, pooling, bound IR retention, runtime API or new dependency is introduced. Each context is emission-local, as its writer and existing mutable collections already require; independent concurrent contexts remain supported.

Line directives append their formatted number, separator and cached path directly into the writer. The number retains the previous current-culture formatting and is prepared before the path literal. Directives remain unindented at any generated scope depth. Required output bytes, line directives, mappings and runtime calls remain identical.

## Work and allocation

This is a constant-factor/common-path pass, not a new overall asymptotic class. Stable name construction and path escaping happen once per requesting context instead of once per occurrence. Emitting A directives with P-character paths still writes O(A*P) output; caching preparation does not eliminate that unavoidable output cost. Binary source-line lookup and allocation of output/mapping records remain unchanged.

Four additional nullable reference fields enlarge each context even when no getter is called. First use still allocates names; used names remain reachable until their context is collected. This bounded emission-local retention may cost more for cold tiny documents than repeated spelling. No retained-process-heap improvement or global memory bound is claimed from allocation counters. The zero-read and one-read context probes measure this setup cost explicitly.

## Verification

`EmissionBookkeepingTests` freezes the original mapping implementation independently of the changed helpers, covering all eight line/suppression/failure combinations with empty and escaped paths, nested changes, clamped spans, arbitrary UTF-16, cancellation, culture-sensitive directive spelling and writer positions. Other cases verify shared-service replacement/restoration, stable name identity, warmed zero-allocation reads and independent concurrent compilable emitted documents.

The manifest retains all 460 previous workloads and adds 22 (482 total). Three prebound getters measure repeated ordinary name reads. Three fresh-context cases perform zero, one or 1,000 sets of name reads; context construction, hashing, lazy allocation and disposal are timed, reflection/delegate compilation is not. Sixteen complete public CSharpEmitter fixtures cross 1/512 objects, named/unnamed objects, short/long-escaped paths and line directives off/on. They bind and compile successfully outside timing and add exact source, mapping and diagnostic signatures.

The same probe source compiles independently against both revisions. Existing alternating process pairs, raw samples, SDK and source identities, full-catalog normal-analyzer/full-captured-C# comparisons and byte-identical generated-source requirements remain. No former workload, assertion, timeout or test gate is removed.

```sh
python3 scripts/compare-fast-paths.py --baseline-ref 04f87b2be025ed7d257723504ad508fd6aa3aba3 --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py --baseline-ref 04f87b2be025ed7d257723504ad508fd6aa3aba3 --iterations 3
```

Actual C# results must come from the exact candidate's .NET execution and CI. Timing and allocation changes, especially tiny/first-use paths, previously adverse named-field cases, and full Csc CPU/wall cost, must be reviewed before merge. No universal build gain or XamlX parity follows from a repeated-getter microbenchmark.
