# Typed markup-assignment sharing

This follows the allocation/complexity pass in PR #20. Its full-catalog results
showed modest generation improvements but essentially unchanged full Csc time.
This transformation instead reduces the generated operations Roslyn must bind,
lower and analyze at repeated markup-extension assignments.

## Transformation

A document-local discovery pass groups ordinary property assignments by receiver
and member identity, the closed constructor and ProvideValue method, explicit
result casts, initialization behavior and ordered extension-property operations.
Occurrence lookup uses reference identity for both owner and assignment; it does
not invoke recursive equality on bound IR records. Only shapes occurring at least
twice receive helpers, and helpers are published in first-use order.

Each helper is an ordinary statically typed C# method. The call supplies the
actual target/frame, descriptor, extension node key, source index and inert
scalar arguments. The helper body is lowered by the existing assignment/object
emitters with parameter substitutions, not a parallel hand-written runtime,
reflection dispatcher or instruction interpreter.

The original descriptor is evaluated before the helper call. Constructor and
property parameters must be exact scalar types, string-to-object reference
conversions, or null string/object references. User conversions, boxing of value
types, narrowing and service-dependent expressions are not hoisted. Within the
helper, construction ownership, BeginInit, ordered property setters, EndInit,
ProvideValue, result conversion, destination setter and edit registration retain
the original order. Exceptions propagate to the existing construction cleanup.
Fresh extension instances and occurrence-specific source metadata remain intact.

Source-info callbacks, named extensions, ref-returning providers, custom setters,
non-constant arguments and different namespace scopes retain the existing lowering.
These are optimization exclusions, not removed language features. Explicit cast
chains are part of the shape identity. Literal source mappings and line directives
remain at each occurrence instead of referring to the shared body's first use.

`XamlCompilerOptions.ShareMarkupAssignments` defaults to `true`; setting it to
`false` preserves the unshared lowering for differential tests and custom hosts.
The option affects generated shape, not bound semantics or runtime functionality.

## Validation

`MarkupAssignmentTests` compiles and executes the same inputs with sharing both
on and off, standalone and in a multi-document compilation. It checks target/root
services, distinct nodes, source ownership, editing and reverse disposal, as well
as failures in descriptor evaluation, construction, BeginInit, extension property
assignment, EndInit, ProvideValue and the destination setter.

The performance probe builds against each compiler revision in isolated output
directories. It includes emission of 100/1,000 repeated markup assignments,
fresh generated-C# parsing/binding/compilation, and warmed runtime graph creation
plus session disposal. Source bytes, IL bytes, local slots, method bodies and
exception regions are reported independently. IL counts include the unchanged
handwritten model. The synthetic Roslyn compilation does not load analyzers; the
full-catalog baseline/candidate comparison continues to use normal analyzers.

The cross-revision correctness hash includes the runtime markup-node/source
metadata, not the intentionally different generated-source bytes for that fixture.
The existing named-view source/source-mapping hashes and syntax/parser mutation
snapshots retain their strict equivalence checks. Full themes, headless catalog,
actual desktop lifetime, trimmed browser execution and package validation remain
separate acceptance requirements.

No performance ratio is implied by source reduction alone. Inspect the actual
`compiler-performance-comparison` artifact for the tested head, SDK and runner.
