# Direct C# compilation optimization

The acceptance target is XamlG's added XAML compilation cost at most half of
XamlX's, including compilation and analysis of generated C#. Full rebuild time,
Roslyn-reported generation time and captured-C# compilation are reported
separately. The target is **not met**. See [measurement methodology](performance.md)
and [PR #14](https://github.com/wieslawsoltes/XamlG/pull/14) for current results.

The [CI checkpoint for `c504074`](https://github.com/wieslawsoltes/XamlG/actions/runs/37927420677)
uses three forced Release rebuilds per compiler/project with SDK 10.0.401 and
normal analyzers. Complete catalog validation passes, including the trimmed
browser host, but the performance target fails for all three projects:

| Project | XamlX added cost | XamlG added cost | G/X added cost | Required XamlG cost |
| --- | ---: | ---: | ---: | ---: |
| Simple | 2.796s | 7.373s | 2.64× | ≤1.398s |
| Fluent | 3.887s | 11.853s | 3.05× | ≤1.944s |
| ControlCatalog | 8.448s | 17.639s | 2.09× | ≤4.224s |

The [separate profile run for the same head](https://github.com/wieslawsoltes/XamlG/actions/runs/37927420454)
reports generation at 1.207/1.599/2.958 seconds and captured-C# compilation at
4.806/7.053/14.587 seconds for Simple/Fluent/ControlCatalog. These use a different
runner and are not additive phase costs. Compilation and analysis of the
generated program remain the larger cost. Both runs use PR merge checkout
`cd5c83c51b6e42ca69c606c8ab351826a81da2a8`; all 390 XamlG-generated source hashes
match the local `c504074` snapshot. The local experiments below use
separately identified snapshots and do not establish the acceptance target.

## Structural findings

The comparison uses Avalonia `a9429a328057befa287ffb5e981f58b86a86eda0` and its
XamlX submodule `d7e37ca63dc9b13cdc95ca165938d4904fa0eddf`.

| Finding | Direct C# implementation |
| --- | --- |
| XamlX's `NewObjectEmitter` evaluates arguments directly on the IL stack. | Emit exact scalar literals directly. Keep typed temporaries for other arguments to preserve overload selection and conversion timing. A broader expression-suffix trial reduced source but did not consistently improve compilation. |
| XamlX pools typed temporary locals. | Reuse a typed local after its assignment lifetime ends, within its declaration scope. Exclude closure-sensitive and custom raw-code documents. |
| Context setup is shared in the IL backend. | Share generated context creation, namescope initialization and completion, with fresh instances and document-specific namespace data. |
| Resource aliases repeat the same small construction and deferred-lifetime body. | Share ordinary typed construction and deferred markup helpers; pass each occurrence's constants, node key and source index. Preserve initialization, services, editing registration and failure cleanup. |
| Avalonia's color intrinsic evaluates its public parser during compilation and emits a packed value. | Use the copied, private color parser in the generator; emit `Color.FromUInt32` or a fresh brush constructor with numeric arguments. HSL/HSV literals use numeric constructors. |
| Roslyn still parses, binds, lowers and analyzes the generated program. | Measure captured output with normal analyzers separately. Reducing generator allocations alone cannot remove this cost. |

The pinned [XamlX constructor emitter](https://github.com/kekekeks/XamlX/blob/d7e37ca63dc9b13cdc95ca165938d4904fa0eddf/src/XamlX/IL/Emitters/NewObjectEmitter.cs),
[typed local pool](https://github.com/kekekeks/XamlX/blob/d7e37ca63dc9b13cdc95ca165938d4904fa0eddf/src/XamlX/TypeSystem/XamlLocalsPool.cs),
and [Avalonia literal intrinsics](https://github.com/wieslawsoltes/Avalonia/blob/a9429a328057befa287ffb5e981f58b86a86eda0/src/Markup/Avalonia.Markup.Xaml.Loader/CompilerExtensions/AvaloniaXamlIlLanguageParseIntrinsics.cs)
are the source comparisons. Roslyn's [stack optimizer](https://github.com/dotnet/roslyn/blob/main/src/Compilers/CSharp/Portable/CodeGen/Optimizer.cs)
constrains which locals can remain on the evaluation stack; declaring fewer C#
locals does not by itself prove smaller IL or faster compilation. The actual
assemblies and fresh compiler processes must be measured.

On the complete Simple assembly, local reuse and shared context setup reduced
XamlG's method bodies from 2,300 to 1,302, total local slots from 19,464 to 7,933,
and IL bytes from 676,763 to 603,067. The corresponding XamlX assembly has 524
method bodies, 1,759 local slots and 251,699 IL bytes. These are whole-assembly
counts, including common handwritten C#; XamlG also retains editing metadata,
construction ownership and failure cleanup. Counts describe structure, not a
runtime or compiler speedup.

The next construction-sharing round reduces Fluent's generated C# from
12,392,791 to 10,970,468 bytes. Its largest resource document shares 1,021
instances of one deferred markup body. Each entry retains its own statically
typed function and literal arguments; the generated helpers contain normal
constructors, setters and `ProvideValue` calls. There is no instruction stream
or interpreter. Framework source-info callbacks, custom descriptors, names,
initialization callbacks and observable argument conversions retain the normal
construction path. The corresponding Fluent assembly has 10.5% fewer IL bytes
(1,032,505 → 923,806), 31.5% fewer local slots (19,414 → 13,293), and 1,021 fewer
exception handlers (1,951 → 930).

Trimming dataflow is the largest reported analyzer contributor in the current
profile. The [upstream analyzer](https://github.com/dotnet/runtime/blob/main/src/tools/illink/src/ILLink.RoslynAnalyzer/DynamicallyAccessedMembersAnalyzer.cs)
analyzes generated operation blocks, and its
[local dataflow engine](https://github.com/dotnet/runtime/blob/main/src/tools/illink/src/ILLink.RoslynAnalyzer/DataFlow/LocalDataFlowAnalysis.cs)
iterates over reachable local functions until state converges. This supports
reducing repeated method bodies and exception regions as a structural target.
Analyzer times overlap; they are not additive parts of Csc wall time. Normal
trimming analysis remains enabled in measurements and validation.

## Transformation audit

The current pinned `AvaloniaXamlIlCompiler` registers the same 38 document
transformations and two resource-group transformations represented in the
[native transform inventory](avalonia-transform-audit.md). Rechecking registration
names found no additional pass missing from that inventory. The portable
`XamlCompiler` and `XamlImperativeCompiler` passes correspond to the existing
directive, intrinsic, constructor, member, content/whitespace, conversion,
deferred-content and initialization binders. XamlG lowers its bound model to C#;
it does not execute the XamlX IL backend.

Registration coverage does not prove every transformation is optimal. In
particular, a grammar check followed by generated `Parse` is weaker than the
upstream color constant transformation: it repeats parsing at runtime and emits
unnecessary calls and temporaries. The copied parser replaces that path. Its
[original source manifest and adaptation notes](../src/XamlG.Frameworks/Avalonia/Parsing/README.md)
make the port reviewable independently of the emitter changes.

## Literal-parsing audit

The [complete parser coverage inventory](avalonia-parser-coverage.md) tracks the
current implementation, including public parsers outside the catalog workload.
The counts below preserve the earlier checkpoints; the final parser round is
reported at the end of this document.

This inventory counts actual generated `Parse` call sites at scalar-inlining
revision `5fc7563`, before the color port. It counts source occurrences rather
than unique values or runtime executions. The decimal row was missing from the
original inventory and was added from the fresh `fdec691` capture.

| Parser family | Simple | Fluent | ControlCatalog |
| --- | ---: | ---: | ---: |
| Color | 176 | 319 | 2,465 |
| Geometry / StreamGeometry | 64 | 71 | 294 |
| Easing | 7 | 5 | 25 |
| Cue / IterationCount / KeySpline | 13 | 26 | 1 |
| TransformOperations | 2 | 18 | 1 |
| KeyGesture / Cursor | 5 | 4 | 20 |
| BoxShadows | 1 | 4 | 4 |
| Rect / PixelRect | 2 | 4 | 1 |
| FontFeature | 0 | 0 | 5 |
| HsvColor | 0 | 0 | 3 |
| DateTime | 0 | 0 | 2 |
| Decimal | 0 | 0 | 37 |

After the color, animation/tokenizer, keyboard/cursor and font-feature/shadow ports, actual source captures contain
zero calls to `Color.Parse`, `HsvColor.Parse`, `Easing.Parse`, `Cue.Parse`,
`IterationCount.Parse`, `KeySpline.Parse`, `Rect.Parse`, `PixelRect.Parse`,
`KeyGesture.Parse`, `Cursor.Parse`, `BoxShadows.Parse` or `FontFeature.Parse` in all
three projects. This removes 3,090 runtime parser call sites from this inventory.
The transform-operation port removes another 21 sites. The subsequent decimal
folding targets the 37 newly identified calls. The complete `b902960` capture has
468 calls: 429 geometry, 37 decimal and two date/time. Fresh inventories are
recorded below and in the PR.

Animation parsing preserves the upstream percentage and suffix grammar and
constructs fresh easing/key-spline objects. Key-spline constructors accept values
that property setters reject, so spline easing uses the parsed-key-spline
constructor overload. Numeric literals use the copied tokenizer instead of a
second token-list implementation. Tests compare accepted values, rejected input
and source-info behavior against the public framework parsers and XamlX loader.

The keyboard port preserves aliases, modifier ordering, numeric enum values and
`Ctrl++` parsing. Cursor lowering retains the source-info distinction between
the case-sensitive intrinsic and the case-insensitive parser fallback.
Text-object conversion now precedes content whitespace normalization, matching
XamlX and preserving the cursor metadata for space-padded literals. Pristine
keyboard/cursor source is isolated in `74204d1`; adaptations are in `f7088a6`.

The font-feature and shadow import is isolated in `3b92450`; adaptation `0bec59d`
adds typed initializer IR and emits ordinary C# object initializers, including
font features' init-only properties. Values that require statement lowering
construct first and assign in order. Tests cover constructor/setter ordering,
user conversions, failure short-circuiting, value types and target services.
The copied parsers retain invalid font-feature defaults, index overflow, signed
zero, shadow-list count semantics and bracket/token grammar. Generated applications
reference their public framework types and have no dependency on the private parsers.
All 2,268 native tests and all 1,188 catalog cases per headless, actual desktop and
trimmed browser host pass. All 19 original parser source hashes are verified.

Removing the last nine shadow and five font-feature parse sites adds only
242/971/1,521 generated bytes to Simple/Fluent/ControlCatalog. This trades a small
amount of typed construction source for eliminating those runtime parsers; it
does not establish a compiler speedup by itself.

Three alternating fresh compiler pairs isolate this parser change, with normal
analyzers and a frozen seven-assembly generator. Local host load varied.

| Project | Generation before → after | Full Csc CPU before → after | Captured C# CPU before → after |
| --- | ---: | ---: | ---: |
| Avalonia.Themes.Simple | 1.020s → 1.061s | 10.949s → 9.855s | 8.143s → 7.737s |
| Avalonia.Themes.Fluent | 1.413s → 1.291s | 15.687s → 15.454s | 13.332s → 14.473s |
| ControlCatalog | 3.741s → 3.710s | 31.060s → 33.088s | 26.729s → 23.946s |

The phase results are mixed, including higher full Csc CPU for ControlCatalog
and higher captured-C# CPU for Fluent. The change removes runtime parsing; this
comparison does not establish a general compiler speedup or the 2× target.

Further ports must preserve the parser's accepted grammar, invariant-culture
behavior, numeric rounding, constructor semantics and fresh object ownership.
For geometry, expanding every path segment into construction statements can
increase generated size and C# analysis cost substantially. Measure both build
cost and runtime construction before choosing a lowering. Dynamic strings and
user-defined conversion code still require their runtime conversion contracts.

## Immutable binding metadata

The pinned [Avalonia property-info emitter](https://github.com/wieslawsoltes/Avalonia/blob/a9429a328057befa287ffb5e981f58b86a86eda0/src/Markup/Avalonia.Markup.Xaml.Loader/CompilerExtensions/XamlIlClrPropertyInfoHelper.cs)
caches property descriptors. XamlG now also shares typed `ClrPropertyInfo`
construction and immutable compiled-binding paths. The cache key includes closed
member types, signatures, write access and index values. Static typed getters and
setters remain in generated C#; every binding creates its own accessors,
expression nodes and subscriptions. Paths requiring a namescope or other runtime
services stay local. Failed initialization can retry without poisoning a static
constructor. Incremental publication moves helper ownership when the original
document fails or is removed.

Across Simple, Fluent and ControlCatalog, generated descriptor constructors fall
from 43/53/406 to 27/27/237 and path-builder constructors from 170/193/607 to
120/127/473. Helpers contribute individually identified members to one partial
cache class per assembly. This reduces repeated runtime construction but does
not by itself establish a compiler improvement.

Three alternating local pairs compare the keyboard/cursor revision with this
cache implementation. Generation is Roslyn-reported elapsed time; full and
captured C# columns are fresh-process user plus system CPU. Normal analyzers
remain enabled. The host was under variable load and swapping.

| Project | Generated bytes before → after | Generation before → after | Full Csc CPU before → after | Captured C# CPU before → after |
| --- | ---: | ---: | ---: | ---: |
| Simple | 6,900,684 → 6,917,868 | 0.832s → 0.887s | 8.609s → 9.154s | 7.708s → 7.952s |
| Fluent | 10,971,115 → 10,968,368 | 1.311s → 1.239s | 16.730s → 15.757s | 12.865s → 14.143s |
| ControlCatalog | 23,837,148 → 23,966,173 | 2.747s → 1.934s | 28.168s → 26.101s | 25.019s → 21.326s |

These results are mixed: Simple regresses, Fluent's captured C# regresses and
ControlCatalog improves. Generated size also grows for Simple and ControlCatalog.
There is no claim of a general compile-time speedup or achievement of the 2×
target. The subsequent CI acceptance run for `66c749c`, including this cache
change, reports XamlG/XamlX added-cost ratios of 2.10/2.99/1.92; the required ratio
is at most 0.5. Its separate profiling run reports generation medians of
1.185/1.549/2.942 seconds and captured-C# medians of 4.839/7.722/13.954 seconds.

Validation covers 2,245 native tests, including independent two-way targets,
subscription replacement, distinct index arguments and closed generic members,
template namescopes, lazy-construction failure and incremental helper ownership.
All 1,188 catalog cases also pass on each of headless, actual desktop and trimmed
browser hosts.

## Larger construction bodies and accessor reuse

Inherited ordinary CLR properties now use their resolved declaring type in the
editing accessor. This allows different derived controls to share the same
getter/setter implementation while retaining virtual dispatch and keeping hidden
members and closed generic members distinct. Custom setters retain their
receiver and conversion contracts. The immutable accessor strings and their key
are cached with weak symbol keys, so the cache does not retain old compilations.

A second construction-sharing pass handles deferred objects with one adapted
markup assignment, including brushes whose color comes from a resource. The
shared method contains the ordinary typed constructor, initialization, extension
construction, `ProvideValue`, adapter dispatch and completion/cleanup sequence.
Only exact scalar constants, node keys and source-table indices become parameters.
Each invocation constructs fresh objects, frames and subscriptions. Per-occurrence
argument mappings and line directives remain in the caller. Names, custom
conversions, nested namespace scopes, init-only setters and framework source-info
callbacks retain the original emission path.

Focused tests check initialization order, root and parent services, independent
source nodes and editing, dynamic-resource updates, owned subscriptions and failed
construction cleanup. Both source-info modes are exercised. The full native suite
contains 2,250 tests after these additions. All assertions pass. The final full
run hit a test-platform symbol-reader cleanup exception after Tooling's tests
passed; its isolated rerun exits successfully. All 1,188 catalog cases pass on
each of the headless, actual desktop and trimmed browser hosts.

Three alternating local compiler pairs compare the binding-cache revision with
the combined accessor and larger-body changes. Each process uses a frozen set of
all seven generator dependencies and the normal analyzers. The host was under
variable load and swapping; these diagnostics do not replace CI acceptance.

| Project | Generated bytes before → after | Generation before → after | Full Csc CPU before → after | Captured C# CPU before → after |
| --- | ---: | ---: | ---: | ---: |
| Simple | 6,917,868 → 6,793,012 | 0.795s → 0.745s | 7.352s → 7.017s | 7.363s → 7.435s |
| Fluent | 10,968,368 → 10,331,886 | 1.639s → 1.662s | 20.440s → 16.702s | 17.534s → 18.404s |
| ControlCatalog | 23,966,173 → 23,775,364 | 5.795s → 5.354s | 42.645s → 37.894s | 39.331s → 36.045s |

Simple shares 48 deferred object bodies through four methods; Fluent shares 248
through eight. The generated source contains 44/240 fewer construction-failure
handlers respectively. Full Csc CPU medians improve in this local comparison,
but captured-C# CPU is slightly worse for Simple and Fluent, and Fluent generation
is approximately flat. These are mixed phase results, not a 2× speedup claim.
The corresponding whole Fluent assembly shrinks from 917,430 to 861,154 IL bytes
(6.1%), from 13,490 to 10,368 local slots (23.1%), and from 930 to 690 exception
handlers. Handwritten code is included in these structural counts.

## Type-name binding allocations

The subsequent CI allocation profile identifies repeated metadata-name probes
and XAML type-name parsing. Named-type probes now compare the cached constructed
type's metadata name directly; generic arguments do not change that name. Simple
XAML type names bypass list/argument builders, and non-generic leaves use empty
immutable argument lists. Whitespace, lists, nullable suffixes and invalid input
retain the full parser. Tests cover nested generics, nullable annotations,
concurrent reads, source spans and malformed inputs.

An isolated warmed allocation probe over 100,000 calls reduces simple-name
parsing from 320 to 48 bytes per call, and positive metadata probes from 40 to 20
bytes per call for its constructed/nullable symbol fixture. These are operation
measurements, not whole-compiler allocation totals or acceptance timings.

All 390 generated files across the three captured projects have identical
SHA-256 hashes before and after this change. The 2,270-test native validation
passes across the assemblies; the aggregate run reported an Avalonia dispatcher
ownership error during one test's headless cleanup. A complete isolated rerun of
all 1,444 Avalonia tests exits successfully. The unchanged generated sources are
the same sources validated in the preceding complete catalog run.

Three alternating local pairs with normal analyzers produced mixed timings:

| Project | Generation before → after | Full Csc CPU before → after | Captured C# CPU before → after |
| --- | ---: | ---: | ---: |
| Simple | 1.019s → 0.982s | 9.388s → 10.227s | 7.527s → 7.564s |
| Fluent | 1.392s → 1.440s | 14.322s → 15.436s | 12.716s → 12.390s |
| ControlCatalog | 2.084s → 2.275s | 25.663s → 25.557s | 23.146s → 22.474s |

The allocation reduction is confirmed by the isolated probe. This busy-host
comparison does not establish an end-to-end compiler improvement; generation
medians are worse for Fluent and ControlCatalog, and full Csc CPU is worse for
both themes. The 2× added-cost target remains unmet.

## Experiments that were not retained

Local trials of lexical assignment blocks, fixed-size assignment helper chunks,
generic construction guards, deferred-factory hoisting and extra typed setter
helpers did not consistently improve complete Csc CPU time; some worsened
trimming analysis. Smaller source alone is not sufficient evidence. Keep the
normal analyzers enabled, alternate before/after runs, retain raw measurements,
and recheck the full XamlX acceptance benchmark before reporting a win.

The broader argument-expression trial removed 2,644 argument assignments from
Simple and reduced its source from 6,911,945 to 6,768,502 bytes. Three alternating
local measurements still showed increased captured-C# CPU medians for Simple and
ControlCatalog, with Fluent approximately unchanged. Full Csc CPU also failed to
improve consistently. The host was under variable load, so these results do not
prove an inherent regression, but they do not justify shipping the added emitter
complexity as a performance improvement. The trial was reverted; its evaluation
order and overload regressions remain.

A separate captured-C# type-alias experiment shortened frequent qualified names
without changing the generated operations. Source size fell from 6.90/10.95/23.77
MB to 6.16/9.59/21.60 MB for Simple/Fluent/ControlCatalog. Three alternating pairs
with normal analyzers gave captured-C# CPU medians of 8.340→7.752,
12.123→13.290 and 21.430→22.279 seconds. Because Fluent and ControlCatalog did not
improve, this rewrite was not added to the emitter. Shorter identifiers alone do
not remove Roslyn's operation analysis and code emission work.


## Typed transform-operation lowering and single service emission

Pristine transform-parser import `697d6c0` and adaptation `0d32a2b` move the
remaining operation-list parsing into the generator. A private recording builder
retains the upstream parser's ordered numeric operations. New framework-independent
`BoundBuilderExpression` IR emits public typed builder construction, append calls
and the final result call. This preserves primitive operation kinds and animation
interpolation instead of collapsing the list to a matrix. The generated code has
no dependency on the private parser or recorder.

Differential tests compare values, operation matrices, interpolation, shared
identity, fresh non-identity resources, source metadata and invalid syntax against
Avalonia and XamlX. The upstream final-matrix-value whitespace behavior, unit rules
and rejection of scientific notation remain intact. Invalid operations now report
a source diagnostic during generation. Empty XAML attributes retain upstream
whitespace handling. The tests also exposed and fixed source lookup at adjacent
XML element boundaries, without changing editor cursor lookup behavior.

Typed builder tests cover mutable value types, argument conversions, statement
lowering, call order and failure short-circuiting. All 2,291 native tests pass with
warnings treated as errors. All 20 upstream parser source hashes are verified.
All 1,188 catalog cases pass on each of headless, actual desktop and trimmed
browser hosts using the pinned source-built Avalonia revision.

Commit `b902960` eliminates duplicate service, namespace-map and runtime-context
emission. Previously, `CreateShared` emitted the complete body for hashing and
then emitted it again with the computed class name. The body now has a fixed type
name inside a content-addressed namespace and is emitted once. No user literals
are rewritten. Existing tests verify independent namespace maps, roots, target
objects, namescopes, rebuilds and shared-source ownership after a document fails.
The `fdec691` profile attributed 7.8/7.7/29.3 MB of sampled allocations to the
nearest `NamespaceMapEmitter.EmitFactory` frame in Simple/Fluent/ControlCatalog;
these are sampled inclusive attributions, not exclusive allocation totals.

An isolated `CreateShared` probe runs 200 warmups and five blocks of 1,000
emissions per variant, using the same bound document and symbols. Complete source
matches after normalizing the generated service identities. Median allocation
per emission falls from 356,129 to 230,278 bytes with one extra namespace (35.3%)
and 622,873 to 398,499 bytes with 32 (36.0%). Corresponding local times are
135.5 to 57.3 and 213.2 to 134.8 microseconds. The probe includes context creation
and reflection overhead, excludes binding and C# compilation, and does not
establish an overall generator or project speedup.

Three alternating fresh compiler pairs compare frozen `fdec691` and `b902960`
assemblies on macOS ARM64, SDK 10.0.401, with normal analyzers. Owned validation
and runtime probes finished before compiler timing. Local host load varied.

| Project | Generated bytes before → after | Generation before → after | Full Csc CPU before → after | Captured C# CPU before → after |
| --- | ---: | ---: | ---: | ---: |
| Avalonia.Themes.Simple | 6,793,254 → 6,792,944 | 0.935s → 0.944s | 9.354s → 10.044s | 6.079s → 6.328s |
| Avalonia.Themes.Fluent | 10,332,857 → 10,333,668 | 1.207s → 0.941s | 15.009s → 11.808s | 9.930s → 10.143s |
| ControlCatalog | 23,776,885 → 23,774,910 | 1.678s → 1.690s | 21.608s → 23.227s | 21.512s → 18.734s |

Generation is Roslyn-reported elapsed time; CPU is user plus system time. Full
Csc wall medians are 4.249→4.149, 6.031→5.272 and 9.336→8.780 seconds; captured
C# wall medians are 2.573→2.595, 3.536→3.506 and 7.445→8.259 seconds. Results
are mixed: Simple and ControlCatalog full Csc CPU increase, while Fluent improves;
Fluent generation improves but the other two are approximately flat. This
isolates the transform/service changes, before the subsequent decimal folding.
It does not establish the XamlX acceptance target.

## Rejected deferred factory consolidation

Three direct-C# alternatives attempted to remove each occurrence's tiny static
factory wrapper. A delegate-backed design increased warmed Fluent construction
allocation by 28%. Static argument tables reduced that increase to 1.3%, but
increased cold JIT cost: the largest population method's JIT time rose from
29.2 to 81.5 ms while successful inlining rose from zero to 2,321 calls. Splitting
the initializers and preventing argument-constructor inlining did not fix that
tradeoff.

The final prototype stored typed arguments directly in a generated resource
subclass, without static tables or a separate argument object. Fluent source
shrunk from 10,332,857 to 9,779,190 bytes (5.4%). Three alternating fresh compiler
pairs nevertheless measured captured-C# CPU at 9.441 to 9.737 seconds and full
Csc CPU at 12.182 to 12.502 seconds; generation was 1.037 to 1.030 seconds.
Other project results were mixed under variable local host load. Warmed Fluent
allocation increased by 4.2%, and cold construction/resolution CPU rose by about
11%/8%. These results do not justify replacing the existing factories. All three
prototypes were removed; the compiler snapshots, JIT logs and runtime probes are
retained in the local performance artifacts for follow-up research.


## Decimal constant folding

A fresh full-source audit at `b902960` found 37 invariant decimal parser calls in ControlCatalog,
which the previous family inventory omitted. Valid decimal literals now pass through
the portable constant parser and emit ordinary C# decimal constants. Decimal scale
is retained; negative zero uses the typed bit constructor because `ToString` drops
its sign. Invalid literals keep their Parse-based runtime failure timing, and
explicit string conversion inside a text-initialized decimal remains a runtime
conversion. This preserves the pinned XamlX invalid-value contract.

Commit `7d402cc` implements this lowering. Sixteen focused tests compare every decimal bit for nullable and non-nullable
properties, including trailing zeros, rounding, range limits, signed zero and
invariant parsing under French culture. They also cover invalid/overflow failures
and explicit string conversion. All pass. The full native run also passes all
2,307 tests with warnings treated as errors. The subsequent checkpoint below
includes full catalog validation and elimination of the remaining workload
parser calls.

## Complete parser coverage

Implementation `49fce1a` covers every public string-parser type in the pinned
Avalonia.Base and Avalonia.Controls assemblies. The executable inventory has 49
explicit cases, discovers unlisted parser APIs, compiles each generated program
and checks actual C# invocation syntax for runtime `Parse` calls, including
escaped identifiers. It runs against both packaged Avalonia 12.1.3 and the newer
ControlCatalog source pin. Behavioral differential suites supplement these
structural checks; [the coverage inventory](avalonia-parser-coverage.md) records
all families and the necessary runtime-conversion boundaries.

Pristine numeric/font parser imports are in `0cb9dbd`, with adaptations in
`02964e9`; path/effect/cache imports are in `f71e556`, with adaptations in
`9b9fa41`; decoration imports are in `ee72643`, with adaptations and discovery
checks in `79975d2`. All 38 original source hashes match the pinned upstream tree.
DateTime folding is isolated in `49fce1a`: complete invariant, zone-free dates
become exact ticks/Kind constructors. Inputs depending on the runtime date or
time zone, and invalid values with existing runtime-failure contracts, keep
their runtime conversion.

Geometry lowering records the copied parser's drawing operations at compile
time, then emits ordinary typed context calls inside a C# disposal scope. No
runtime parser, instruction interpreter or private compiler type is emitted.
The public PathGeometryContext constructor preserves figures on both supported
framework versions. A non-progressing malformed close-command sequence is
diagnosed instead of hanging the compiler. Tests compare complete path figures,
real Skia geometry measurements, source metadata and fresh ownership, and cover
scope disposal and call failures.

All 2,385 native tests pass without warnings, failures or skips. The pinned-source
headless suite passes all 14 tests, and all 1,188 catalog cases pass on each of
headless, actual desktop and trimmed browser hosts. Compiler timing runs start
after this validation completes.

Fresh real-Csc captures compare the frozen seven-assembly generator at `8449f34`
with `49fce1a`. All 390 generated C# files compile. A complete output scan,
including escaped `@Parse` identifiers, finds zero remaining runtime parser
calls in these three projects:

| Project | Parse sites before → after | Generated bytes before → after | Size change |
| --- | ---: | ---: | ---: |
| Simple | 64 → 0 | 6,792,944 → 6,958,291 | +2.4% |
| Fluent | 71 → 0 | 10,333,668 → 10,498,990 | +1.6% |
| ControlCatalog | 296 → 0 | 23,770,914 → 24,908,942 | +4.8% |

This removes the last 429 geometry and two DateTime parse sites in the workload.
It does not remove runtime conversions for arbitrary application values or the
documented context-dependent cases. Expanding paths increases source size;
parser elimination alone is not a compiler-performance improvement.

Three alternating fresh compiler pairs on macOS ARM64, SDK 10.0.401 and
`DOTNET_PROCESSOR_COUNT=2` measure generation and generated-source compilation
separately. Full Csc retains every generator and analyzer. Captured C# replaces
only XamlG with the exact generated source and retains other generators,
including Avalonia's generators, and normal analysis/trimming checks. No owned
build, test or runtime-validation process overlaps these runs. Other host load
varied substantially.

| Project | Generation before → after | Full Csc CPU before → after | Captured C# CPU before → after |
| --- | ---: | ---: | ---: |
| Simple | 1.983s → 2.151s | 14.766s → 14.449s | 10.398s → 11.792s |
| Fluent | 3.636s → 2.509s | 26.166s → 23.789s | 21.155s → 21.598s |
| ControlCatalog | 3.536s → 4.943s | 30.867s → 36.101s | 36.299s → 35.218s |

Generation is Roslyn-reported elapsed time; CPU is process user plus system time.
Full Csc wall medians are 9.062→8.702, 17.705→15.843 and 15.157→18.163 seconds.
Captured-C# wall medians are 5.032→6.247, 12.107→12.476 and 21.603→18.521 seconds.
These independent measurements are not additive phases, particularly under
variable host load. Captured-C# CPU increases for both themes; ControlCatalog
generation and full Csc CPU increase. The mixed results do not establish a
compiler speedup. The 2× XamlX added-compilation-cost target remains unmet.

Local evidence is retained in `artifacts/controlcatalog-performance/all-parsers-phases/`
with per-run logs, source hashes, frozen generator assembly hashes, parser counts
and raw CPU/wall/generator measurements. The CI acceptance and profile runs below
use separate Linux runners and must not be combined with these local timings.

The completed [CI acceptance run](https://github.com/wieslawsoltes/XamlG/actions/runs/37915050254)
uses clean PR merge checkout `ce100b0bcba8f13687272074ba73d4e3887e98e4` for
`49fce1a`, Linux x64 and SDK 10.0.401, with three rebuilds per backend/project.

| Project | XamlX median wall | XamlG median wall | XamlG / XamlX | XamlX compiler tasks | XamlG compiler tasks |
| --- | ---: | ---: | ---: | ---: | ---: |
| Avalonia.Themes.Simple | 7.619s | 13.175s | 1.73x | 4.776s | 9.556s |
| Avalonia.Themes.Fluent | 9.235s | 17.942s | 1.94x | 6.414s | 14.400s |
| ControlCatalog | 18.170s | 29.179s | 1.61x | 14.845s | 25.036s |

Sequential warm-cache forced Release rebuilds, one project at a time. Dependencies restored/built before timing; BuildProjectReferences=false, UseSharedCompilation=false, MSBuild maxcpucount=1. New dotnet/compiler processes per measurement. No restore, framework build, browser publish or application execution in timed region. Includes project evaluation, resources, C# and XAML compilation, and output copying; not an isolated XAML parser benchmark.

| Project | Common C# baseline | XamlX added cost | XamlG added cost | XamlG / XamlX added cost | 2× target met |
| --- | ---: | ---: | ---: | ---: | :---: |
| Avalonia.Themes.Simple | 2.069s | 2.666s | 7.487s | 2.81x | No |
| Avalonia.Themes.Fluent | 2.382s | 4.032s | 12.018s | 2.98x | No |
| ControlCatalog | 6.012s | 8.758s | 19.024s | 2.17x | No |

Added XAML cost is estimated relative to the XamlX build's C# stage: XamlG = median(Csc with XamlG) - median(Csc with XamlX); XamlX = median(CompileAvaloniaXamlTask). The common C# baseline includes Avalonia's normal C# generators, including name generation. The XamlG difference includes generation and compilation/analysis of generated C#, not just generator execution. This is a difference of measured task times, not an independently timed XamlG phase; differences near the measurement noise floor are inconclusive. The target is XamlG added cost <= half XamlX added cost for all three projects; negative differences do not establish a pass.

The separate [CI profile](https://github.com/wieslawsoltes/XamlG/actions/runs/37915050114)
uses the same checkout and SDK on another runner:

| Project | XamlG generation | All generators | Complete Csc | Captured C# + analyzers |
| --- | ---: | ---: | ---: | ---: |
| Avalonia.Themes.Simple | 1.684s | 2.285s | 9.634s | 7.128s |
| Avalonia.Themes.Fluent | 2.296s | 2.894s | 13.728s | 10.855s |
| ControlCatalog | 4.474s | 5.868s | 24.078s | 20.559s |

Medians of fresh compiler processes. Generation uses Roslyn's reported elapsed times from the actual full compiler run. Captured C# is a separate run replacing XamlG with its generated sources, retaining other generators and analyzers. It includes C# parsing, binding, analysis, emission and compiler startup. These columns are not additive phases: generated and ordinary syntax trees can behave differently in Roslyn. Analyzer elapsed times overlap and must not be subtracted from Csc wall time. Use the full XamlX benchmark for acceptance.

This leaves required XamlG added costs of at most 1.333s, 2.016s and 4.379s
for Simple, Fluent and ControlCatalog respectively. None of the projects meets
the accepted 2× target. The captured-C# measurements remain substantially larger
than generation, so reducing emitted operations and their analysis cost remains
the primary performance work alongside generator allocation improvements.

## Shared scalar assignments

Repeated scalar assignments previously emitted descriptor evaluation, the typed
setter and editing registration at every occurrence. These operations can share
an ordinary typed C# method, with the target, context and scalar as parameters.
The method evaluates the descriptor before boxing or nullable wrapping, calls
the original setter and registers the same editing accessor. Virtual dispatch,
source locations, per-object ownership and failure cleanup remain intact.
Constant-dependent narrowing, enum-zero conversions, user conversions, custom
setters and name registration keep their existing emission paths.

Eligible public shapes share across documents through the existing immutable
property tables; private or document-generated descriptors stay local. The
complete helper body contributes to the shared source identity, so changing its
parameter layout invalidates unchanged callers too. The table and helper body
are emitted once and reused when wrapping the content-addressed class, avoiding
the previous second table emission.

An initial local-only prototype produced 1,203 scalar helpers in ControlCatalog.
Sharing across documents reduces this to 121 local/shared helper definitions and
removes 1,177,124 generated bytes compared with the parser-complete baseline.
All generated code remains direct typed C#; there is no instruction backend.

| Project | Generated bytes before → after | Change | Remaining Parse calls |
| --- | ---: | ---: | ---: |
| Simple | 6,958,291 → 6,934,481 | −0.3% | 0 |
| Fluent | 10,498,990 → 10,472,810 | −0.2% | 0 |
| ControlCatalog | 24,908,942 → 23,731,818 | −4.7% | 0 |

The baseline is the frozen `49fce1a` generator, unchanged at documentation commit
`184bc65`. The candidate's seven compiler assemblies and changed source hashes
are frozen under `artifacts/controlcatalog-performance/shared-scalar-generator/`.
The `shared-scalar-phases/` evidence includes all 390 generated files, hashes,
parser counts and three alternating fresh compiler pairs per project. SDK
10.0.401, macOS ARM64, `DOTNET_PROCESSOR_COUNT=2`, normal analyzers and the same
full/captured-C# protocol described above are used. Owned tests and runtime
probes run only after the timed compiler processes finish; other host load varies.

| Project | Generation before → after | Full Csc CPU before → after | Captured C# CPU before → after |
| --- | ---: | ---: | ---: |
| Simple | 0.977s → 1.351s | 10.174s → 9.799s | 7.662s → 8.449s |
| Fluent | 1.012s → 1.111s | 12.217s → 13.045s | 13.213s → 11.180s |
| ControlCatalog | 4.710s → 2.692s | 36.487s → 34.466s | 26.241s → 28.817s |

Full-Csc wall medians are 5.013→5.380, 5.597→6.527 and 16.698→18.023 seconds;
captured-C# wall medians are 3.181→3.410, 5.664→4.145 and 10.324→13.859 seconds.
Generation is Roslyn-reported elapsed time; CPU is user plus system time. These
results are mixed, including increased captured-C# CPU for Simple and the
catalog. The retained source reduction does **not** establish a compilation
speedup or a pass against XamlX. The completed CI checkpoint is recorded below.

A separate runtime probe uses the exact before/after compiler outputs with
identical runtime dependencies. Three alternating fresh process pairs create
SliderPage, CheckBoxPage and TextBoxFirstLookPage as a bundle, or construct
FluentTheme with and without resolving eight Light/Dark resources. Each process
measures one cold bundle, warms 30 bundles, then measures five blocks of 30.
Every constructed root has a session that is disposed; resource counts are
asserted. Application setup is measured separately, outside the page bundle.

| Runtime workload | Cold CPU before → after | Cold bytes, both | Warm CPU/bundle before → after | Warm bytes/bundle, both |
| --- | ---: | ---: | ---: | ---: |
| Three catalog pages | 41.645ms → 36.290ms | 334,096 | 0.2953ms → 0.2474ms | 142,515 |
| Fluent construction | 77.849ms → 72.241ms | 1,891,856 | 2.0878ms → 1.9832ms | 771,492 |
| Fluent plus eight resources | 90.598ms → 89.927ms | 1,985,808 | 2.7611ms → 2.9966ms | 813,089 |

Rows report process medians, with each warm value first taking the median of its
five blocks. Allocation is measured on the current thread. Catalog application
setup CPU is 235.478→237.587ms. Allocation medians are unchanged, but the resource
workload's warm CPU increases, so this small probe is not evidence of a universal
runtime speedup. Raw evidence and the probe are in `shared-scalar-runtime-probe/`.

Eight new tests cover local sharing, descriptor/setter failure order, boxing,
nullable values, conversion fallbacks, source mappings, live editing and shared
layout changes during incremental compilation. The latter runs with both one
and four compiler workers, compares incremental output with fresh compilation,
and checks surviving documents after a shared source's owner is removed.

All 2,393 native tests pass with warnings treated as errors: 433 core, 1,525
Avalonia, 169 tooling, 94 language-server, 158 automation and 14 workspace tests.
No tests fail or skip. This includes the exhaustive parser inventory and the
existing behavioral parser suites. The generated workload retains zero Parse
calls in both snapshots.

At `9e6517d`, the pinned-source suite also passes all 14 tests and all 1,188 cases
pass on each of headless, actual desktop and trimmed browser hosts. Native and
MSBuild CI passes on Linux, Windows and macOS. The completed
[catalog/acceptance workflow](https://github.com/wieslawsoltes/XamlG/actions/runs/37923053195)
uses clean merge checkout `391bf627a2bf5d364b72fda5776f5fa77e5ae86e`, Linux x64,
SDK 10.0.401 and three sequential forced rebuilds per backend/project.

| Project | XamlX rebuild | XamlG rebuild | XamlX compiler tasks | XamlG compiler tasks | Common C# | XamlX added cost | XamlG added cost | G/X added cost |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Simple | 7.888s | 13.771s | 4.901s | 10.074s | 2.104s | 2.799s | 7.970s | 2.85× |
| Fluent | 9.463s | 18.266s | 6.528s | 14.576s | 2.429s | 4.099s | 12.147s | 2.96× |
| ControlCatalog | 18.730s | 28.446s | 15.246s | 24.306s | 6.849s | 8.674s | 17.457s | 2.01× |

Added costs use the same difference-of-medians and compiler-task methodology
above. The 2× target still fails for all projects. The separate
[profile run](https://github.com/wieslawsoltes/XamlG/actions/runs/37923053179)
uses the same checkout on another runner; its 390 XamlG-generated files match
the local candidate hashes exactly. Other generators add 12 files to the
catalog's compiler invocation and remain enabled in the measurements.

| Project | XamlG generation | All generators | Full Csc | Captured C# + analyzers |
| --- | ---: | ---: | ---: | ---: |
| Simple | 0.829s | 1.118s | 4.449s | 3.243s |
| Fluent | 1.092s | 1.381s | 6.190s | 4.933s |
| ControlCatalog | 1.669s | 2.416s | 11.922s | 10.084s |

These columns are not additive, and the profile/acceptance runners must not be
combined. Separate CI runs are not paired before/after experiments.

## Metadata probe allocations

The `9e6517d` allocation profile attributes approximately 4.7/5.2/37.9 MB of
Simple/Fluent/catalog allocations to `HasMetadataName`, principally Roslyn's
construction of arity-suffixed names. The existing qualified-name cache did not
avoid reading `ISymbol.MetadataName` on every suffix test. Generic symbols can
allocate a new short metadata-name string on that read.

Each weak metadata-name entry now caches the short name and computes the
qualified name only when needed. Both positive and negative generic probes reuse
the short string. Constructed types retain their definition names, while nested
names, escaping and non-type original-definition behavior remain unchanged.
Three additional tests cover negative probes followed by concurrent qualified
lookups and collection of old compilations with either short-only or fully
qualified entries.

Fresh-process microbenchmarks load the actual old and candidate assemblies with
SDK Roslyn 5.9.0.0. Three alternating process pairs/triplets warm each case with
10,000 calls, then measure five blocks of 100,000 calls. Rows below take each
process's block median, then the process median. These are metadata-name probes,
not XAML compilation or XamlX acceptance timings.

| Symbols / probe | Milliseconds per 100,000 before → after | Allocated bytes before → after |
| --- | ---: | ---: |
| Generic / positive | 6.7313 → 2.3014 | 4,000,000 → 0 |
| Generic / negative | 4.9301 → 1.5398 | 4,000,000 → 0 |
| Generic / same suffix, wrong namespace | 5.6401 → 1.8606 | 4,000,000 → 0 |
| Ordinary / positive | 2.8725 → 2.1033 | 0 → 0 |
| Ordinary / negative | 1.3430 → 1.6268 | 0 → 0 |
| Ordinary / same suffix, wrong namespace | 2.8511 → 2.0705 | 0 → 0 |

Ordinary negative probes incur additional cache lookup work. A trial restricting
the cache to generic symbols was slower in every one of the nine mixed, generic
and ordinary microbenchmarks, so that extra branch was not retained. Evidence
for all three variants is in `metadata-generic-micro/`; the initial mixed-only
probe is in `metadata-probe-micro/`.

The frozen `metadata-probe-generator/` candidate and `shared-scalar-generator/`
baseline generate **byte-identical C# across all 390 files**, retaining zero
workload Parse calls. Three alternating fresh real-Csc pairs per project use
the same SDK, analyzers and two-processor configuration as the earlier local
measurements. No owned build/test/runtime probe overlaps the timed compilers.

| Project | Generation before → after | Full Csc CPU before → after | Captured C# CPU before → after |
| --- | ---: | ---: | ---: |
| Simple | 1.651s → 2.081s | 15.647s → 16.039s | 12.363s → 13.492s |
| Fluent | 2.336s → 1.889s | 20.879s → 17.945s | 12.881s → 15.809s |
| ControlCatalog | 7.710s → 6.280s | 48.313s → 42.181s | 43.427s → 37.297s |

Full-Csc wall medians are 9.107→10.681, 11.513→8.685 and 33.408→26.314 seconds.
Captured-C# wall medians are 6.060→7.282, 5.147→6.933 and 35.963→25.597 seconds.
The captured source is identical, yet its measured times vary substantially;
host variability therefore prevents attributing these differences to the change.
Simple generation also increases. The retained result is the narrowly measured
allocation reduction, not a demonstrated full-compiler speedup. The accepted
2× XamlX compilation target remains unmet.

All 2,396 native tests pass with warnings treated as errors and no failures or
skips: 436 core, 1,525 Avalonia, 169 tooling, 94 language-server, 158 automation
and 14 workspace tests. All three complete project captures compile with normal
analysis. Generated application sources match the `9e6517d` version already
validated across all 1,188 cases on each host; runtime source is unchanged.

The completed `c504074` CI runs use clean merge checkout
`cd5c83c51b6e42ca69c606c8ab351826a81da2a8`. All eight workflows pass, including
[complete catalog validation and the benchmark](https://github.com/wieslawsoltes/XamlG/actions/runs/37927420677)
and [browser-editor acceptance](https://github.com/wieslawsoltes/XamlG/actions/runs/37927420325).
The catalog workflow passes all 1,188 cases on each host. Its three sequential
forced Release rebuilds per compiler/project report:

| Project | XamlX rebuild | XamlG rebuild | XamlX compiler tasks | XamlG compiler tasks | Common C# | XamlX added cost | XamlG added cost | G/X added cost |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Simple | 7.621s | 12.911s | 4.820s | 9.403s | 2.030s | 2.796s | 7.373s | 2.64× |
| Fluent | 9.122s | 17.792s | 6.285s | 14.251s | 2.398s | 3.887s | 11.853s | 3.05× |
| ControlCatalog | 17.722s | 27.581s | 14.425s | 23.616s | 5.977s | 8.448s | 17.639s | 2.09× |

The [separate profile run](https://github.com/wieslawsoltes/XamlG/actions/runs/37927420454)
reports generation at 1.207/1.599/2.958 seconds, full Csc at
6.313/9.515/16.812 seconds, and captured C# plus analyzers at
4.806/7.053/14.587 seconds for Simple/Fluent/catalog. All 390 XamlG-generated
source hashes match the local `c504074` snapshot. These phases are not additive,
and different CI runners do not form a paired before/after experiment. The
added-cost target still fails in all three projects.

## Shared construction and frame registration

Parameterless construction, construction ownership and source-node registration
now share ordinary typed methods across documents. Each helper calls the
original public constructor, then `PushConstructed`, and optionally `BeginInit`.
Source-table indexing stays after construction. Named fields and namescope
registration remain before `BeginInit`; early collection insertion, assignments,
`EndInit` and final consumption keep their original order. Roots, factory methods,
generic/non-public types and custom framework source-info callbacks keep the
existing path. No interpreter, reflection-based constructor or delegate factory
is introduced.

The helper returns the frame and writes the constructed instance through a typed
`out` argument. That argument uses the existing temporary pool's scope and
capture checks. Complete helper bodies determine shared identities, and the
project publisher moves definitions when their owning document is edited or
removed. A document-local namespace alias shortens calls; source mappings are
adjusted before resource exports prepend their assembly attributes.

The comparison starts at `c504074`. The frozen seven-assembly candidate is
`construction-frame-generator/`; its source hashes match the implementation
validated below. The three workloads use 121/123/211 helper definitions for
2,901/4,104/10,445 construction sites respectively.

| Project | Generated C# bytes before → after | IL bytes before → after | Remaining Parse calls |
| --- | ---: | ---: | ---: |
| Simple | 6,934,481 → 6,914,186 | 602,306 → 572,351 | 0 |
| Fluent | 10,472,810 → 10,426,442 | 887,096 → 845,612 | 0 |
| ControlCatalog | 23,731,818 → 23,009,668 | 2,787,812 → 2,611,729 | 0 |

This removes 788,813 source bytes in total, including 722,150 bytes (3.0%) from
the catalog, and 247,522 IL bytes (5.8%) across the complete assemblies. IL counts
include common handwritten code. Method bodies increase by the helper counts;
total local slots increase by one per assembly and exception-region counts are
unchanged. All 390 XamlG-generated source files compile with normal analyzers.

Three alternating fresh real-Csc pairs per project use SDK 10.0.401, macOS ARM64
and two reported processors. Owned builds, tests and runtime probes finish before
these timings; other host load varies. `construction-frame-phases/` retains the
response files, generated sources/hashes, logs and measurements.

| Project | Generation before → after | Full Csc CPU before → after | Captured C# CPU before → after |
| --- | ---: | ---: | ---: |
| Simple | 0.903s → 0.892s | 9.014s → 9.496s | 6.712s → 6.353s |
| Fluent | 0.993s → 1.056s | 14.352s → 13.848s | 11.519s → 13.446s |
| ControlCatalog | 2.122s → 1.787s | 26.833s → 26.633s | 25.039s → 24.429s |

Full-Csc wall medians are 3.982→3.968, 5.504→6.481 and 11.245→13.501 seconds;
captured-C# wall medians are 2.485→2.641, 4.062→5.163 and 10.407→10.658 seconds.
The results are mixed: Simple full CPU and Fluent captured CPU increase. The
retained result is smaller typed C# and IL, **not a demonstrated compiler speedup**
or a pass against XamlX. Generation and captured compilation are independent,
nonadditive measurements.

The same fresh-process runtime protocol used for scalar sharing compares the
actual `c504074` and candidate assemblies: three alternating pairs, one cold
bundle, 30 warmups, then five blocks of 30 bundles, disposing every root session.
With default tiered compilation, measurements are:

| Runtime workload | Cold CPU before → after | Warm CPU/bundle before → after | Cold bytes, both | Warm bytes/bundle, both |
| --- | ---: | ---: | ---: | ---: |
| Three catalog pages | 36.386ms → 34.296ms | 0.2850ms → 0.2266ms | 334,112 | 142,515 |
| Fluent construction | 89.811ms → 92.948ms | 2.3408ms → 2.7895ms | 1,891,880 | 771,492 |
| Fluent plus eight resources | 106.175ms → 70.672ms | 3.1453ms → 1.9735ms | 1,985,848 | 813,089 |

The warm Fluent-construction CPU increase is about 19%. A separate controlled
comparison disables tiered compilation (`DOTNET_TieredCompilation=0`) so methods
use optimized code from their first compilation. It uses fresh processes and
the same repetitions; it is a different runtime configuration, not a replacement
for the default-runtime result:

| Runtime workload | Cold CPU before → after | Warm CPU/bundle before → after | Cold bytes, both | Warm bytes/bundle, both |
| --- | ---: | ---: | ---: | ---: |
| Three catalog pages | 88.410ms → 89.753ms | 0.0706ms → 0.0735ms | 383,184 | 142,491 |
| Fluent construction | 125.774ms → 127.458ms | 0.9698ms → 0.9817ms | 1,891,904 | 771,492 |
| Fluent plus eight resources | 142.377ms → 142.752ms | 1.0246ms → 1.0090ms | 1,998,216 | 813,089 |

Allocations remain identical in every scenario/configuration. Optimized warm
differences are small, but neither experiment establishes a universal runtime
speedup. Rebuildable probes and raw results are in `construction-frame-runtime-probe/`
and `construction-frame-runtime-optimized/`.

A second prototype placed all helpers in one shared partial type per project.
It passed the 16 focused tests but increased full compiler CPU in every project
in a separate three-pair comparison against the first prototype. The
Simple/Fluent/catalog full CPU medians were 11.938→12.152, 12.895→13.844 and
26.414→32.747 seconds; captured CPU was 8.848→11.509, 11.588→10.862 and
22.770→25.892 seconds. Fewer types did not establish a compilation benefit.
That layout was rejected and the validated first prototype restored. Evidence
is retained in `construction-flat-generator/` and `construction-flat-phases/`.

All 2,412 native tests pass with warnings treated as errors and no failures or
skips: 452 core, 1,525 Avalonia, 169 tooling, 94 language-server, 158 automation
and 14 workspace tests. Sixteen new project-compiler cases exercise construction,
initialization/setter/collection failures, cleanup, early consumption, both name
representations, custom source callbacks, editing, source mappings and incremental
helper ownership with one/four workers. The pinned suite passes all 14 tests,
and all 1,188 catalog cases pass on each of headless, actual desktop and trimmed
browser hosts. The exhaustive parser inventory remains green.
