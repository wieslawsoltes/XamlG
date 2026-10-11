# Project-finalization allocation pass

Publication baseline: `a83921eca1bbdc037a1515af0d6f7e7b514d58ba`.
The frozen oracles were captured from `b686b73`; intervening IntelligentUI edits
do not change project finalization. The active member-binding PR #33 is independent.

This pass changes project-level duplicate bookkeeping and shared-helper publication.
It does not change binding, custom directive evaluation, IR traversal, lowering,
generated runtime calls, source mappings, or helper definitions. Exact generated
source equivalence is an acceptance requirement, not an assumed benchmark result.

## Duplicate declarations

`Compile` already sorts input by ordinal logical path. `CollectSortedPaths` uses
that order to detect repeated active paths in one scan. Skipped occurrences do not
interrupt an active duplicate pair. Every input still calls the directive policy:
neither discovering a duplicate nor recognizing a sorted run suppresses callbacks.
The duplicate-path set is allocated only when a duplicate is found.

Resource-URI and class-name validation used separate LINQ `GroupBy` pipelines.
The replacement keeps a first-index dictionary for each declaration kind and
allocates a dense boolean collision array only when that kind has a collision.
There are no per-key group objects or arrays. No dictionary is allocated for a kind
with no eligible declarations, and a project of fewer than two documents needs no
URI/class duplicate index at all.

Both implementations have expected O(N) bookkeeping time. The change is primarily
allocation and constant-factor work, not a new asymptotic class. Arrays are bounded
by the project's existing document limit. Comparisons remain ordinal, with null
URI/class names excluded exactly as before. A document keeps existing diagnostics,
then URI-duplicate diagnostics, then class-duplicate diagnostics. Rootless and
skipped handling and source spans remain unchanged. Immutable record identity is
preserved where no diagnostic is added.

## Shared helper publication

The first successful occurrence of a helper identity still owns its definition.
Duplicate identities inside one document and identities previously claimed by an
earlier document remain suppressed. No shared definition is moved into a failing
output, and cached base output is never mutated.

A cached document now remembers the immutable selection of helper objects rather
than a concatenated identity string. While the base output is the same immutable
record, those helper references are stable. A warm publication compares accepted
helpers against that selection as it fills the project-local identity set; when
ownership matches, it returns the already-decorated output without creating a
selected-source array, concatenated identity string, or LINQ predicate iterator.
The project-wide identity set still exists: this is not zero-allocation compilation.

A changed prefix, shortened selection or new owner creates a new immutable
selection and source wrapper. Only at that miss does the existing newline/join
formatting run. Losing all helpers temporarily leaves the remembered wrapper
available for restoration. An emission that is not the cache entry's base output
is published independently and cannot replace the cache entry's remembered output.
Regeneration clears the selection and wrapper together. The owning compiler already
serializes calls; workers finish emission before publication begins.

The selection retains references to existing helper records, not caller arrays,
bound nodes, symbols or a static/global publication map. There is one remembered
selection per cached document; `ClearCache`/document removal releases it. Cold and
ownership-changing passes can allocate a small selection builder plus its immutable
copy. That setup/retention cost must be compared with the removed identity string
and grouping allocations before accepting a performance claim.

Cancellation checks precede scanning and cache publication. No partial selection
is stored for the current document on cancellation. Completed earlier documents
may retain valid decorated outputs; the next call always reconstructs first-owner
selection from its actual current document order.

## Tests and differential evidence

`ProjectDuplicateDiagnosticsTests` keeps the previous grouped validator as an
independent oracle, covers 250 seeded mixed declaration sets, and tests original
record identity, skipped inputs, URI-before-class order, rootless spans, custom
policy invocation order/exceptions and cancellation.

`SharedSourcePublisherTests` freezes the previous publisher with separate cache
state, exercises 300 seeded ownership changes, stable snapshot/output reuse,
first-occurrence selection, empty source bodies, shrink/growth/loss/restoration,
noncached emissions and pre-cancelled publication.

`ProjectFinalizationIntegrationTests` covers complete public project compilation
with serial/parallel options and resource exports enabled/disabled. Successful
edit/removal/restoration results must match a fresh compiler and their generated
C# must compile. Duplicate path/URI/class sequences must recover without stale
source, mappings or diagnostics.

There are 25 new C# cases. A separate stdlib-only Python transition model has six
tests, including exhaustive small duplicate/owner states and 10,000 seeded ownership
transitions. The model is not execution or validation of the C# implementation and
does not replace its .NET tests.

## Benchmark scope

The same candidate probe is compiled independently against each revision. Eighteen
new workloads cover cold and no-op public project compilation at 1/32/256 documents,
with empty objects and shared typed properties; first-owner removal/restoration;
and combined duplicate logical paths, URIs and class declarations. Export is disabled
to avoid repeated root-export probing masking the finalization work, but these are
still full public `XamlProjectCompiler.Compile` calls, not isolated finalization
microbenchmarks. Removal/readdition includes any rebind or layout work it causes.
Input construction and correctness compilation are untimed. Every successful
fixture is compiled, and complete output/diagnostic/mapping snapshots join the strict
cross-revision semantic signature. Invalid duplicate fixtures require diagnostics
and are not represented as executable generated programs.

The manifest now pins 103 workloads. All prior probes, normal-analyzer catalog
comparisons, operation-count/SDK/commit/source checks and exact generated-source
inventory checks remain enabled. Tiny/cold cases intentionally expose overhead;
there is no claimed measured speedup until actual candidate results are reviewed.

```sh
dotnet build XamlG.slnx -c Release -warnaserror -p:NuGetAudit=false
dotnet test XamlG.slnx -c Release --no-build --logger trx
python3 scripts/test-project-finalization-model.py
python3 scripts/test-compiler-performance-gates.py
python3 scripts/verify-workflow-inventory.py
python3 scripts/compare-fast-paths.py --baseline-ref a83921eca1bbdc037a1515af0d6f7e7b514d58ba --pairs 3
python3 scripts/prepare-controlcatalog.py
python3 scripts/compare-controlcatalog-compilers.py --baseline-ref a83921eca1bbdc037a1515af0d6f7e7b514d58ba --iterations 3
python3 scripts/verify-compiler-performance.py \
  --baseline a83921eca1bbdc037a1515af0d6f7e7b514d58ba --candidate "$(git rev-parse HEAD)" \
  --require-identical-sources --workload-manifest tools/XamlG.PerformanceProbe/workloads.json
```

Use empty result directories for a repeat comparison. Merge also requires the
repository's applicable native/MSBuild platform, upstream, original-theme, package,
host, workspace, complete ControlCatalog and browser gates to pass for the actual
proposed merge tree. Earlier PRs' successful CI is not candidate acceptance. No
assertions, workflows, timeouts or public APIs were weakened by this pass.
