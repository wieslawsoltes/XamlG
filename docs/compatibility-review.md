# Compiler compatibility and performance review

This review follows the completed transform inventory with a deeper comparison of implicit conventions, duck typing, runtime services and generator work. The reference implementations are portable XamlX `7ef6aef496ab6e8dcf3df04bef697be49db37c04`, Avalonia `8eeda4f6f546165b3f72e63c9f42247abb306905`, and the Avalonia 12.1.3 compiler/runtime packages. Existing checkpoint results remain evidence for their exact source revisions; they do not close this review.

For each area, inspect the upstream implementation and integration points, compare executable behavior, correct differences, and retain the relevant regression evidence. Performance changes require measurements before and after the change, with semantic and incremental correctness checks. A source mapping or a passing unrelated corpus is insufficient to close an area.

| Area | Review status and required evidence |
| --- | --- |
| Namespace/type resolution | Open: intrinsic names, namespace/assembly mappings, forwarders, generic/nested types, accessibility and ambiguity. |
| Implicit content and metadata | Open: content discovery/inheritance, property versus direct collection content, whitespace and initialization metadata. |
| Collection mutation and duck typing | First corrections verified: declared `IAddChild` interfaces, typed/object ordering, ordinary interface `Add` precedence, inaccessible helpers and non-void adders. Continue checking replacement, overload conversion and runtime dispatch alongside these contracts. |
| Markup providers | Open: duck-typed providers, suffix lookup, parameter/return choices, inheritance, construction and target-service context. |
| Constructors and initialization | Open: argument inference/conversion, service constructors, root/populate differences, initialization order and failure cleanup. |
| Properties and events | Open: CLR/attached/registered members, qualified/hidden members, assignment alternatives and event/root-method binding. |
| Conversions and text | Open: intrinsic and runtime conversions, converters and services, list/numeric/enum grammar, nullable values and evaluation order. |
| Runtime services | Open: root/intermediate root, provide-value target, parent-stack protocols, namespaces, URI/type-descriptor context and custom provider composition. |
| Deferred content and names | Open: template result types, service/parent capture, sharing, namescopes, forward references and disposal. |
| Avalonia styles and bindings | Open: revisit every corresponding entry in the transform inventory, including implicit scopes, data-type inference, duck-typed methods and framework binding integration. |
| Resources and project linking | Open: eager/deferred/merged/theme resources, includes, exported factories, source information and invalidation. |
| Build and markup integration | Open: compiler/build directives, generated fields/initializers, loader adaptation, package settings and application consumers. |
| Diagnostics and recovery | Open: upstream warnings/errors, location/phase, malformed input, cancellation and recovery after edits. |
| Performance | Initial generator baseline and metadata-cache improvement measured; see [the harness and results](performance.md). Continue reviewing runtime parent traversal and other demonstrated repeated work without retaining symbols across compilations. |
| Final validation | Pending the full review: native/upstream suites, original theme construction, MSBuild/package/host consumers and production browser checks at recorded revisions. |

## Collection findings

`RoslynTypeSystem.CollectAddMethods` previously searched child protocols only through `AllInterfaces`, omitting a property's declared interface itself. A property typed as non-generic `IAddChild` became read-only to the compiler; the portable generic interface fell back to its object-valued base contract. Ordinary interface `Add` methods and child protocols were also interleaved, although upstream puts all ordinary adders first, then generic child contracts, then object child contracts.

The corrected discovery includes the declared interface, preserves ordinary adder ordering, appends typed child protocols before object fallbacks, and excludes inaccessible interface helper methods. Thirteen shared `ImplicitCollectionTests` execute the same assertions through both compilers; ten `ImplicitChildContractTests` additionally compare the actual Avalonia interfaces. They cover explicit implementations, implicit content, literal/provided values and dispatch order.

Source inspection suggested that an enumerable backed by a `HashSet` might require a runtime list cast. Execution disproved that assumption: the upstream emitted call uses the declaring `ICollection<T>` mutation method and succeeds. The shared regression preserves this behavior, as well as duck-typed `Add` methods with a non-void return and no enumeration interface.

Local investigation and regression artifacts are under `artifacts/tests/compatibility-review/implicit-collections/`. Early failing runs are retained separately from corrected assertions and the repaired compiler.
