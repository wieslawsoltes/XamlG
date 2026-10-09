# Direct C# compilation optimization

The acceptance target is XamlG's added XAML compilation cost at most half of
XamlX's, including compilation and analysis of generated C#. Full rebuild time,
Roslyn-reported generation time and captured-C# compilation are reported
separately. The target is **not met**. See [measurement methodology](performance.md)
and [PR #14](https://github.com/wieslawsoltes/XamlG/pull/14) for current results.

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

## Remaining literal-parsing audit

This inventory counts actual generated `Parse` call sites at scalar-inlining
revision `5fc7563`, before the color port. It counts source occurrences rather
than unique values or runtime executions.

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

After the color, animation/tokenizer and keyboard/cursor ports, actual source captures contain
zero calls to `Color.Parse`, `HsvColor.Parse`, `Easing.Parse`, `Cue.Parse`,
`IterationCount.Parse`, `KeySpline.Parse`, `Rect.Parse`, `PixelRect.Parse`,
`KeyGesture.Parse` or `Cursor.Parse` in all three projects. This removes 3,076
runtime parser call sites from this inventory. The remaining 466 calls cover
geometry (429), transforms (21), box shadows (9), font features (5) and date/time (2).

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
target. The latest completed CI acceptance run, before this cache change, reports
XamlG/XamlX added-cost ratios of 2.58/2.98/2.13; the required ratio is at most 0.5.

Validation covers 2,245 native tests, including independent two-way targets,
subscription replacement, distinct index arguments and closed generic members,
template namescopes, lazy-construction failure and incremental helper ownership.
All 1,188 catalog cases also pass on each of headless, actual desktop and trimmed
browser hosts.

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
