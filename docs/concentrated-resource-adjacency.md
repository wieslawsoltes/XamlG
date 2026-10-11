# Concentrated resource adjacency

This continues the blocked reverse-graph pass. The original block candidate reduced allocated bytes on graph-heavy inputs but its 1,024-document cold fan-in median regressed 55.8% in that checkpoint. No merge or causal attribution is inferred from passing correctness tests alone.

The representation now recognizes an initial sequence of edges to one target. During that phase, only the bounded edge blocks are allocated: an edge's successor is implicitly the next ordinal. A second destination triggers allocation of the document endpoint array, initialized with the first destination's head and tail. The initial prefix remains implicit permanently; there is no edge-copy or relinking walk during promotion. Subsequent links preserve ordinary append order, including later edges back to the first destination.

Construction remains expected O(D + E) overall. Single-destination storage avoids the O(D) endpoint array. Promotion costs O(D) zero-initialization, independent of the preceding edge count. Enumeration remains allocation-free and retains complete first-reference descriptors. Runtime lowering, source mappings, graph membership, cycle decisions and failure priority are unchanged. This is neither CSR nor pooled storage.

The additional state and per-edge mode branches are real costs on distributed graphs. Six new validator benchmarks at 32 and 1,024 documents cover warm, backend-only and cold late-promotion topologies; every previous workload remains in the 208-name manifest. Tests exercise empty/single-target modes, all block boundaries, late transitions, return to the original destination, equal-but-distinct descriptors, invalid targets and complete competing-failure outcomes against the frozen validator.

Measurements must use complete paired before/after reports and matching semantic signatures. Final source/native/full-catalog/browser validation applies to the actual final head, not the prior all-block checkpoint. Cold fixtures include root/document copies and dependency discovery, not cold-process startup. No universal build gain or XamlX parity is claimed.
