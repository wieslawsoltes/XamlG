# Namespace-map emission planning

This increment follows the exact-output traversal, lifetime and metadata work in
PRs #28 and #29. It changes compiler-side planning, not generated runtime behavior.

## Repeated work removed

Previously every namespace factory sorted its declared aliases, and every service
contract/alias filtered the complete document mapping list and formatted all matching
item initializers again. Shared service identity calculation and local publication
also recomputed lexical scope keys.

An emission-local `NamespaceEmissionPlan` now owns an ordered snapshot per scope,
an ordinal mapping index, and generated array expressions per service contract/URI.
`ServiceContractEmitter` and `NamespaceMapEmitter` use the same context-owned plan.
The mapping list is indexed once, and each used URI's initializer is formatted once
per contract rather than once per occurrence across all scopes.

For M mappings and A alias occurrences across factories/contracts, selection changes
from O(A * M) filtering to expected O(M + A), plus matching-record formatting and
output-writing costs. Ordering a distinct scope's P declared prefixes still costs
O(P log P), but the resulting array/key is reused. The emitted bytes necessarily
remain proportional to the original source size: this is not output compression.

Plans allocate only when namespace services are used. Indexing and caching add
memory and setup work, especially for tiny inputs. The benchmark explicitly measures
the tiny case; overall performance is not inferred from the complexity bound alone.

## Preserved semantics and ownership

The exact previous ordinal scope key, explicit-prefix filtering, input mapping
order, duplicate records, null/empty assembly spelling, dictionary protection,
helper identity and factory naming remain unchanged. Two aliases for the same URI
reuse source text, not runtime data: every emitted occurrence still contains its
own `new` array and item expressions. No alias array or namespace item is shared
where the original factory created separate instances.

The plan has no static state. It cannot mix documents, mappings or service contracts,
and it dies with its emission context. Parallel documents have independent mutable
plans. Snapshot/index/text publication occurs only after cancellation checks; canceled
plan reads do not return already-cached results.

## Quality gates

`NamespaceFactoryOracle` is a frozen copy of the previous factory lowering. Tests
compare complete generated factories on cold/warm reads for shadowed prefixes,
implicit/explicit XML, missing mappings, duplicate records, escaping, two service
contracts and protected/unprotected dictionaries. Additional tests compile and run
factories to verify distinct alias arrays/items, isolate document mappings, exercise
cancellation and check concurrent per-document output determinism.

The identical baseline/candidate probe adds six workloads to the 57-workload manifest:
factory-only and complete document emission at 1 scope / 2 aliases / 8 extra mappings,
32 scopes / 16 aliases / 512 extra mappings, and 128 / 16 / 512. The mapping set contains
unused URIs as well as multiple records for used URIs. Every operation constructs a
fresh emission context. Binding, delegate-bridge compilation and generated-code
compilation checks are outside measured intervals. Both factory source and complete
document source/mappings/diagnostics enter the mandatory cross-revision signature.

All existing native/MSBuild, upstream, theme, package, host, browser and full catalog
checks remain enabled. The performance gate requires all 63 pinned workloads, complete
paired measurements, matching semantic signatures and byte-identical full catalog
source. No speedup, universal cold-build improvement or XamlX parity is claimed without
exact-head evidence. The local environment has no .NET SDK: local Python validation
must not be described as a C# build or execution result.
