# Repeated member binding and assignment identity

This pass follows the resource-graph optimization. It targets compiler binding, not generated runtime behavior. Generated C# and source maps remain subject to the exact-output comparison policy.

## Selection, not bound-member caching

`MemberBinder.Resolve` previously recreated `OfType`/predicate/first-result pipelines on every ordinary property or event occurrence. Immutable member-list and accessibility caches already existed, but the selection work and iterator/delegate allocation still repeated.

`InstanceMemberCache` reuses only the selected Roslyn symbol (including a missing result). It never caches a `BoundMember`, source span, namespace scope, diagnostic, conversion, target descriptor, rule invocation or attached-property overload choice. Property and event occurrences still append their own symbol metadata; getter/setter checks still use the current context; member rules execute on every successful resolution. Missing-member diagnostics remain conditional on each call's `report` flag and location.

The key contains the member spelling, target symbol identity and access-context symbol identity. Reference identity is deliberate: default Roslyn symbol equality can ignore nullable annotations and tuple element names that custom rules can observe on the selected member. Different `RoslynTypeSystem` instances cannot share selections. This avoids conflating distinct constructed symbols, at the cost of fewer hits when Roslyn produces fresh equivalent wrappers.

An emission-independent, weak type-system key owns the selection table. Although selections contain symbols and the cache uses the type system to check accessibility, that value/key cycle is confined to the conditional weak table. No document or syntax snapshot is stored. The thread-safe cache publishes at most 4,096 selections and retains keys only for names up to 256 UTF-16 units. Negative lookups use the same budget. Publication reserves capacity atomically; concurrent computations for the same key release duplicate reservations.

Shallow cold misses scan Roslyn's immutable declared-member arrays directly, preserving class/base/interface order. ALL eligible properties are considered before ANY event: a derived event can coexist with a base property under the existing XAML resolution contract. Static properties/events and indexers remain excluded exactly as before. At saturation, for long names, or for deep shapes, selection uses the existing weak candidate-list cache rather than repeatedly walking deep inheritance. Saturation affects reuse only; it does not change validity or selection.

Cancellation is checked on hits, at hierarchy boundaries and before publication. The public binding context is still document-local and mutable; this cache does not make a single `BindingContext` concurrently usable.

For repeated keys, selection work changes from repeated scans to one selection plus expected constant-time hits. The compilation already cached member lists, so this is not a claim that all of binding changes asymptotic complexity. Cold unique keys pay table setup/publication costs; bounded overflow and small-document paths are measured explicitly.

## Preserve duplicate-assignment string identity

`MemberBinder.AddSet` used `member.Symbol.ToDisplayString()` to build its duplicate-assignment identity on every successful assignment. `AssignmentMemberName` reuses the exact same text under a weak symbol-identity key. Values hold strings only; the symbol/compilation is not retained after its ordinary owners disappear.

The target's public `AssignedScalars` set remains ordinal strings. Replacing it with a symbol set would change behavior when custom rules supply distinct symbols with the same legacy spelling. Conversion validation still precedes identity insertion. Duplicate errors, per-occurrence spans, `AllowRepeatedAssignments` and independent target sets remain unchanged. Formatting is tested against the previous expression for generics, nested types, escaped names, attached setters and cultures.

## Quality gates

The selector regression oracle is the previous LINQ expression over the original `Members` traversal and actual Roslyn accessibility. Tests cover cold/warm and concurrent contexts, hidden/static/indexer cases, generic/interface/value-type/type-parameter targets, private/protected members, context changes, nullable and tuple distinctions, owner-qualified lookup, rule/diagnostic replay, bounded concurrent saturation, long names, cancellation and weak ownership.

Assignment-key tests cover exact spelling, cross-compilation equal-text collisions, repeated-assignment policy, independent/parallel targets, cultures and weak compilation lifetime. Existing framework, runtime, project invalidation, source mapping, upstream, theme, native/MSBuild, browser, package and full ControlCatalog suites remain enabled.

## Reproduction and interpretation

```sh
dotnet test XamlG.slnx -c Release
python3 scripts/compare-fast-paths.py --baseline-ref b257a2a8018060c0bca501e59904745057425792 --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py --baseline-ref b257a2a8018060c0bca501e59904745057425792 --iterations 3
```

The integrated probe adds 18 member workloads to the 126 already on main, bringing the pinned manifest to 144. All use public compiler APIs and identical probe sources against both revisions. Setup, delegate construction, model compilation, semantic snapshots and generated-fixture compilation are untimed. The warm lookup cases include fresh `BoundMember` and symbol-occurrence creation. Timed contexts clear their symbol list per operation; capacities are warmed by the shared harness.

- Ordinary lookup: one/repeated/unique/missing keys; warm and cold type-system/context cases; a 32-level inherited target; a saturated cache with 1,000 uncached deep misses.
- Duplicate-assignment identity: 1,000 repeated assignments with target state cleared between assignments, and 1,000 distinct assignments on one target. Conversion validation and `BoundSetAssignment` creation remain timed.
- Complete binding: 32 or 1,024 children with eight ordinary properties each, using warm or fresh type systems. Roslyn model/syntax remain warm in the cold-type-system workloads; these are not fresh-process cold-build claims.

Selected symbols, kinds, getter/setter metadata and occurrence diagnostics join the cross-revision semantic digest. Both full binding fixtures must emit C# and compile successfully before their timed runs; exact generated source, mappings and diagnostics join the digest. The existing full catalog comparison retains normal analyzers, captures all three projects and requires byte-identical generated inventories. Raw samples and small-case/overflow regressions must be reviewed, not hidden by aggregate averages. No benchmark result or XamlX parity is asserted by this design document.


## Final integration and bounded cold discovery

The completed branch integrates main through PR #35 rather than replacing its conversion or project-finalization changes. The probe driver includes every prior signature and the workload manifest is the union; old 101/126-workload reports cannot satisfy the 144-workload gate. Baseline comparison is now the merged main `b257a2a`, not the earlier pre-conversion baseline. Whole-program benchmarks therefore include the interaction between member, conversion and publication caches.

For shallow misses the property scan records whether any instance event exists. With none, no event scan is necessary; accessibility of an event is still checked only after all eligible properties have been exhausted. Static/indexed/inaccessible properties and hidden events preserve the frozen selector's precedence.

A bounded four-ancestor shape probe chooses the existing symbol-owned member-list cache for deep classes or more than four inherited interfaces, before reading any declared-member arrays. This avoids rediscovering an entire deep hierarchy for every new type system over the same Roslyn compilation. It adds at most four ancestor reads to a cold selection, not a walk proportional to inheritance depth. The shallow direct path and the 4,096-entry/256-character selection limits remain. The underlying candidate cache's existing lifetime/retention rules are unchanged; this is not a claim of a global memory limit.

Two additional cold-deep workloads explicitly cover 1,000 missing keys and repeated property/event/missing keys over a 32-level hierarchy. Eleven additional test cases straddle class/interface cutoffs, compare multiple access contexts and fresh type systems with the frozen original selector, and replace project compilations across Parse-converted/string/private-property states in serial and parallel mode. Restored successful generated output must compile and match a fresh compiler's source, mappings and diagnostics. Existing provider/rule, nullability, weak ownership, negative-result and saturation tests remain mandatory.

The extra shape probe and cold entry publication have setup costs. Accept performance only from the final merged-input comparison with all small/unique/deep and ordinary full-binding cases retained. Report cold unique-name regressions and whole-Csc CPU/wall changes as well as hot-cache wins. This integration does not change generated runtime semantics or claim XamlX parity.
