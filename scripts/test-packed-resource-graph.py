#!/usr/bin/env python3
"""Independent models for ordered adjacency and in-place Kahn/failure state.
These tests do not replace execution of the C# validators and frozen oracles.
"""
from collections import deque
import random
import unittest


def adjacency(edges):
    first, last, links = [0] * len(edges), [0] * len(edges), []
    for parent, targets in enumerate(edges):
        for ordinal, target in enumerate(targets):
            links.append([parent, ordinal, 0])
            link = len(links)
            if last[target]:
                links[last[target] - 1][2] = link
            else:
                first[target] = link
            last[target] = link
    result = []
    for head in first:
        row = []
        while head:
            parent, ordinal, head = links[head - 1]
            row.append((parent, ordinal))
        result.append(row)
    return result


def run(edges, failures, reuse):
    incoming = adjacency(edges) if reuse else [
        [(p, o) for p, row in enumerate(edges) for o, target in enumerate(row) if target == t]
        for t in range(len(edges))]
    remaining = list(map(len, edges))
    visited = [False] * len(edges)
    queue = deque(i for i, value in enumerate(remaining) if value == 0)
    order = []
    while queue:
        index = queue.popleft()
        order.append(index)
        if not reuse:
            visited[index] = True
        for parent, _ in incoming[index]:
            remaining[parent] -= 1
            if remaining[parent] == 0:
                queue.append(parent)
    processed = [value == 0 for value in remaining] if reuse else visited
    diagnostics = [[] for _ in edges]
    for i, targets in enumerate(edges):
        if not processed[i]:
            first = next((ordinal, target) for ordinal, target in enumerate(targets) if not processed[target])
            diagnostics[i].append(('cycle', first))
    seeds = [i for i in range(len(edges)) if i in failures or diagnostics[i]]
    marks = remaining if reuse else [False] * len(edges)
    for i in range(len(edges)):
        marks[i] = i in failures or bool(diagnostics[i])
    queue = deque(seeds)
    while queue:
        failed = queue.popleft()
        for parent, ordinal in incoming[failed]:
            if marks[parent]:
                continue
            marks[parent] = True
            diagnostics[parent].append(('failure', failed, ordinal))
            queue.append(parent)
    return order, diagnostics, list(map(bool, marks))


class PackedGraphTests(unittest.TestCase):
    def test_interleaving_matches_independent_append_lists(self):
        randomizer = random.Random(512031)
        for _ in range(1000):
            count = randomizer.randrange(1, 65)
            edges = [randomizer.sample(range(count), randomizer.randrange(count + 1)) for _ in range(count)]
            expected = [[] for _ in edges]
            for parent, row in enumerate(edges):
                for ordinal, target in enumerate(row):
                    expected[target].append((parent, ordinal))
            self.assertEqual(expected, adjacency(edges))

    def test_random_graphs_keep_processing_and_failure_order(self):
        randomizer = random.Random(290114)
        for _ in range(2000):
            count = randomizer.randrange(0, 65)
            edges = [randomizer.sample(range(count), randomizer.randrange(min(count, 8) + 1)) for _ in range(count)]
            failures = {i for i in range(count) if randomizer.randrange(7) == 0}
            self.assertEqual(run(edges, failures, False), run(edges, failures, True))

    def test_acyclic_lattices_keep_queue_order(self):
        for width in (1, 2, 4, 16):
            edges = [list(range(i + 1, min(257, i + 1 + width))) for i in range(257)]
            self.assertEqual(run(edges, {128, 256}, False), run(edges, {128, 256}, True))

    def test_cycles_self_edges_and_callers_remain_unprocessed(self):
        edges = [[1, 2], [1], [], [0, 2], [5], [4], []]
        self.assertEqual(run(edges, {2}, False), run(edges, {2}, True))

    def test_storage_preserves_duplicate_occurrences(self):
        self.assertEqual([[(0, 0), (0, 1), (1, 0)], [(0, 2)]], adjacency([[0, 0, 1], [0]]))

    def test_empty_graph(self):
        self.assertEqual(([], [], []), run([], set(), True))


if __name__ == '__main__':
    unittest.main(verbosity=2)
