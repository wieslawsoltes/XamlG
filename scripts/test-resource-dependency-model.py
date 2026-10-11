#!/usr/bin/env python3
"""Independent algorithm model; this does NOT execute or compile the C# candidate."""
from collections import Counter, deque
from dataclasses import dataclass
import random
import unittest


@dataclass(eq=False)
class Node:
    children: tuple = ()
    uri: str | None = None
    occurrence: int = 0


class Admission:
    def __init__(self):
        self.bits = 0
        self.exact = set()

    def expand(self, node, fingerprint):
        value = fingerprint & 0xFFFFFFFF
        mixed = (value * 0x9E3779B9) & 0xFFFFFFFF
        mixed ^= mixed >> 16
        mask = (1 << (value & 1023)) | (1 << (mixed & 1023))
        possible = self.bits & mask == mask
        self.bits |= mask
        if not possible:
            return True
        if node in self.exact:
            return False
        self.exact.add(node)
        return True


def occurrences(root):
    pending = [root]
    found = []
    pops = 0
    while pending:
        node = pending.pop()
        pops += 1
        if node.uri is not None:
            found.append(node)
        pending.extend(node.children)
    return found, pops


def first_uris(values):
    result = {}
    for node in values:
        result.setdefault(node.uri, node)
    return tuple(result.values())


def compact(root, fingerprints):
    pending = [root]
    admission = Admission()
    found = {}
    expanded = Counter()
    pops = 0
    while pending:
        node = pending.pop()
        pops += 1
        if node.uri is not None:
            found.setdefault(node.uri, node)
        elif node.children and admission.expand(node, fingerprints[node]):
            expanded[node] += 1
            pending.extend(node.children)
    return tuple(found.values()), expanded, pops, len(admission.exact)


def reachable(root):
    seen = set()
    pending = [root]
    while pending:
        node = pending.pop()
        if node in seen:
            continue
        seen.add(node)
        pending.extend(node.children)
    return seen


def graph_outcome(uris, snapshots, initial_failures):
    # Independent simple planner: explicit sets and per-caller first-edge lookup.
    counts = Counter(uri for uri in uris if uri is not None)
    local = {uri: i for i, uri in enumerate(uris) if uri is not None and counts[uri] == 1}
    edges = [[edge for edge in snapshot if edge.uri in local] for snapshot in snapshots]
    dependencies = [set(local[edge.uri] for edge in values) for values in edges]
    reverse = [[] for _ in uris]
    for parent, targets in enumerate(dependencies):
        for target in sorted(targets):
            reverse[target].append(parent)
    queue = deque(i for i, targets in enumerate(dependencies) if not targets)
    visited = set()
    while queue:
        node = queue.popleft()
        visited.add(node)
        for parent in reverse[node]:
            dependencies[parent].remove(node)
            if not dependencies[parent]:
                queue.append(parent)
    diagnostics = [[] for _ in uris]
    failed = set(initial_failures)
    for node in range(len(uris)):
        if node not in visited:
            edge = next(edge for edge in edges[node] if local[edge.uri] not in visited)
            diagnostics[node].append(('cycle', edge.uri, edge.occurrence))
            failed.add(node)
    queue = deque(sorted(failed))
    while queue:
        node = queue.popleft()
        for parent in reverse[node]:
            if parent in failed:
                continue
            failed.add(parent)
            edge = next(edge for edge in edges[parent] if local[edge.uri] == node)
            diagnostics[parent].append(('failed', edge.uri, edge.occurrence))
            queue.append(parent)
    return failed, diagnostics


class DependencyModelTests(unittest.TestCase):
    def test_constant_fingerprint_cannot_false_prune(self):
        admission = Admission()
        nodes = [Node() for _ in range(4096)]
        counts = Counter()
        for _ in range(4):
            for node in nodes:
                counts[node] += admission.expand(node, 0)
        self.assertEqual(counts[nodes[0]], 2)
        self.assertTrue(all(counts[node] == 1 for node in nodes[1:]))

    def test_saturation_does_not_false_prune(self):
        admission = Admission()
        for i in range(1024):
            self.assertTrue(admission.expand(Node(), i))
        self.assertEqual(admission.bits, (1 << 1024) - 1)
        for i in range(4096):
            node = Node()
            self.assertTrue(admission.expand(node, i))
            self.assertFalse(admission.expand(node, i))

    def test_dag_order_matches_frozen_occurrences_under_adversarial_collisions(self):
        rng = random.Random(81467)
        for sample in range(1000):
            pool = [(Node(uri=rng.choice(['u0', 'u1', 'u2', 'U2', 'external']), occurrence=i), 1) for i in range(8)]
            for _ in range(24):
                chosen = [rng.choice(pool) for _ in range(rng.randrange(1, 4))]
                cost = 1 + sum(cost for _, cost in chosen)
                if cost <= 2048:
                    pool.append((Node(tuple(node for node, _ in chosen)), cost))
            root = Node(tuple(node for node, _ in pool[-3:]))
            expected = first_uris(occurrences(root)[0])
            unique = reachable(root)
            for fingerprints in ({node: 0 for node in unique}, {node: rng.randrange(2**31) for node in unique}):
                actual, expanded, pops, _ = compact(root, fingerprints)
                self.assertEqual(expected, actual)
                self.assertTrue(all(1 <= count <= 2 for count in expanded.values()))
                self.assertLessEqual(pops, 1 + 2 * sum(len(node.children) for node in unique))

    def test_depth_128_fanout_dag_is_linear_in_distinct_edges(self):
        for fanout in (2, 3, 5):
            leaf = Node(uri='u', occurrence=19)
            value = leaf
            for _ in range(128):
                value = Node((value,) * fanout)
            unique = reachable(value)
            actual, expanded, pops, _ = compact(value, {node: 0 for node in unique})
            self.assertEqual((leaf,), actual)
            self.assertLessEqual(pops, 1 + 2 * 128 * fanout)
            self.assertLessEqual(max(expanded.values()), 2)

    def test_unshared_tree_has_no_exponential_work(self):
        rng = random.Random(92971)
        def tree(depth):
            return Node((tree(depth - 1), tree(depth - 1))) if depth else Node()
        root = tree(10)
        unique = reachable(root)
        result, expanded, pops, _ = compact(root, {node: rng.randrange(2**31) for node in unique})
        self.assertEqual((), result)
        self.assertEqual(pops, len(unique))
        self.assertTrue(all(count == 1 for count in expanded.values()))

    def test_first_uri_remains_case_sensitive_and_preserves_object_identity(self):
        a, b, c = Node(uri='u', occurrence=1), Node(uri='U', occurrence=2), Node(uri='u', occurrence=3)
        root = Node((a, b, c, a))
        result = compact(root, {root: 0})[0]
        self.assertEqual((a, b), result)
        self.assertIs(result[0], a)

    def test_full_graph_diagnostics_are_invariant_under_compaction(self):
        rng = random.Random(731913)
        for sample in range(2000):
            count = rng.randrange(1, 17)
            uris = [None if rng.randrange(7) == 0 else 'u' + str(rng.randrange(count)) for _ in range(count)]
            snapshots = [[Node(uri='u' + str(rng.randrange(count + 2)), occurrence=sample * 1024 + i * 32 + j)
                          for j in range(rng.randrange(24))] for i in range(count)]
            failures = {i for i in range(count) if rng.randrange(5) == 0}
            self.assertEqual(graph_outcome(uris, snapshots, failures),
                             graph_outcome(uris, [first_uris(values) for values in snapshots], failures))

    def test_repeated_uri_storage_is_bounded_by_unique_uri_count(self):
        root = Node(tuple(Node(uri='target', occurrence=i) for i in range(32768)))
        result, _, _, _ = compact(root, {root: 0})
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0].occurrence, 32767)


if __name__ == '__main__':
    unittest.main(verbosity=2)
