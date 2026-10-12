# Cast-chain formatting and explicit mapping state

Baseline: verified PR #45 merge `4b3be9fa475bd2874eae03522e3ee78c3a8356fd`.

## Consecutive casts

The old `ValueEmitter.Emit` recursively wrapped each cast around a newly allocated
copy of the entire formatted child. For D casts with bounded type-name lengths,
that costs O(D²) copied characters/cumulative allocation and O(D) call stack.
`CastChainEmitter` formats three-or-more consecutive casts with one local builder,
then lowers the terminal expression once and closes every cast. Work/cumulative
text storage becomes amortized O(D + C), where C is the final output size. Unary
cast-only formatting uses constant call-stack depth; nested non-cast expressions
still use their existing lowering and are not claimed stack-safe at arbitrary depth.
Zero/one/two-cast shapes retain the original direct expressions.

No cast is removed or merged. User conversions, converter creation, callbacks,
exceptions, frame/target accesses and independent object results remain occurrences.
Outer type names are formatted before terminal lowering, and cancellation is checked
for each visited cast. No IR node is mutated and no text, context or result is cached.
The builder setup and added shallow-shape tests can cost time for small inputs;
all boundaries remain in the benchmark manifest.

## Mapping callbacks

A generic `EmissionContext.Map<TState>` overload accepts explicit state and a static
callback. Object assignment emission passes a value tuple, removing its per-assignment
closure/delegate and the outer captured state. Mapping arrays/records, line directives
and the generated code still allocate as required; this is not allocation-free emission.

Both old and stateful overloads share Begin/End implementation. Suppression is sampled
at entry because callbacks can change construction parameters. Nested maps make their
own decision. End executes only after callback success: it is intentionally not a
Dispose/finally scope, so exceptions do not publish completed mappings or trailing
`#line default`. Cancellation occurs before directives and callback invocation.
Invocation state is not stored by the mapping helper. No public API, runtime library,
pool, global cache, dependency, code-generation policy or workflow assertion changes.

## Evidence requirements

Twenty-three added C# cases cover frozen formatting at 0/1/2/3/32/256/1,024 casts,
32,768-cast direct-emitter stack depth, cancellation, actual user-defined conversion
calls/failures and fresh runtime values in standalone/shared factories, nested mapping
and suppression transitions with/without line directives, mapping failure behavior,
and exact-zero warmed suppressed static callbacks. Existing full emitter/runtime tests
remain. Deep synthetic formatting is not represented as a successfully compiled deep
C# program; separate shallow fixtures compile and execute.

The identical public/pre-existing-entry-point probe adds 22 cases and retains all 438
predecessors (460 total). Fourteen cast cases cover 0/1/2/3/32/256/1,024 casts, object
and mixed object/string targets. Eight full-emission cases use 32/512 objects with
8 properties, 0/8 casts and line directives off/on. Binding, reflection/delegate setup,
input construction, signature creation and compilation of every successful fixture
are outside the timed region. Full-emission contexts are fresh per operation.

Require three alternating process pairs/five batches, exact semantic/source/mapping/
diagnostic signatures, all three normal-analyzer full/captured-C# catalog comparisons,
and 390 byte-identical generated files. Run the existing native/MSBuild platform matrix,
all browser shards, full ControlCatalog, workspace, package-consumer, original-theme,
upstream and host workflows. Do not use prior-head results as current acceptance.

```sh
python scripts/compare-fast-paths.py --baseline-ref 4b3be9fa475bd2874eae03522e3ee78c3a8356fd --pairs 3
python scripts/prepare-controlcatalog.py
python scripts/compare-controlcatalog-compilers.py --baseline-ref 4b3be9fa475bd2874eae03522e3ee78c3a8356fd --iterations 3
```

Review shallow/builder setup and generic dispatch costs, not only deep cases. Allocated
bytes are not retained-heap measurements. Local source/Python verification is not C#
execution. No speedup, whole-build multiplier or XamlX parity is claimed before exact-
head raw measurements and quality gates complete.
