#!/usr/bin/env python3
"""Independent state-transition checks, NOT execution of the C# implementation.

The actual C# oracle/integration tests and full paired compiler checks remain required.
This stdlib-only model helps audit ordering/ownership where a .NET SDK is unavailable.
"""
from dataclasses import dataclass, field
from itertools import product
import random
import unittest


def legacy_duplicates(keys, skipped):
    groups = {}
    for index, key in enumerate(keys):
        if key is not None and not skipped[index]:
            groups.setdefault(key, []).append(index)
    return {index for group in groups.values() if len(group) > 1 for index in group}


def dense_duplicates(keys, skipped):
    first, flags = {}, None
    for index, key in enumerate(keys):
        if key is None or skipped[index]:
            continue
        if key not in first:
            first[key] = index
        else:
            if flags is None:
                flags = [False] * len(keys)
            flags[first[key]] = flags[index] = True
    return set() if flags is None else {i for i, value in enumerate(flags) if value}


def path_scan(paths, active):
    # Python's ordering is irrelevant: inputs are already sorted in the C# contract.
    result, previous, has_previous = set(), None, False
    for path, compile_it in zip(paths, active):
        if not compile_it:
            continue
        if has_previous and previous == path:
            result.add(path)
        previous, has_previous = path, True
    return result


@dataclass(eq=False, frozen=True)
class Helper:
    identity: str
    source: str


@dataclass
class Slot:
    body: str
    helpers: tuple
    previous: tuple = ()
    published: object = None
    legacy_identity: object = None
    legacy_published: object = None


def old_publish(slots):
    seen, outputs = set(), []
    for slot in slots:
        owned = []
        for helper in slot.helpers:
            if helper.identity not in seen:
                seen.add(helper.identity)
                owned.append(helper)
        if not owned:
            outputs.append(slot.body)
            continue
        identity = '\n'.join(h.identity for h in owned)
        if identity != slot.legacy_identity:
            slot.legacy_identity = identity
            slot.legacy_published = slot.body + '\n' + '\n'.join(h.source for h in owned)
        outputs.append(slot.legacy_published)
    return outputs


def new_publish(slots):
    seen, outputs = set(), []
    for slot in slots:
        changed, count = None, 0
        previous = slot.previous
        for helper in slot.helpers:
            if helper.identity in seen:
                continue
            seen.add(helper.identity)
            if changed is not None:
                changed.append(helper)
            elif count >= len(previous) or previous[count] is not helper:
                changed = list(previous[:count])
                changed.append(helper)
            count += 1
        if not count:
            outputs.append(slot.body)
            continue
        if changed is None and count == len(previous) and slot.published is not None:
            outputs.append(slot.published)
            continue
        owned = tuple(changed) if changed is not None else previous[:count]
        output = slot.body + '\n' + '\n'.join(h.source for h in owned)
        slot.previous, slot.published = owned, output
        outputs.append(output)
    return outputs


class FinalizationModel(unittest.TestCase):
    def test_exhaustive_duplicate_flags(self):
        for length in range(5):
            for keys in product([None, '', 'A', 'a'], repeat=length):
                for skipped in product([False, True], repeat=length):
                    self.assertEqual(legacy_duplicates(keys, skipped), dense_duplicates(keys, skipped))

    def test_sorted_policy_filter(self):
        for length in range(7):
            for keys in product(['a', 'b'], repeat=length):
                paths = sorted(keys)
                for active in product([False, True], repeat=length):
                    accepted = [path for path, keep in zip(paths, active) if keep]
                    expected = {key for key in accepted if accepted.count(key) > 1}
                    self.assertEqual(expected, path_scan(paths, active))

    def test_many_duplicate_diagnostics_keep_per_document_order(self):
        rng = random.Random(481)
        keys = [None, '', 'A', 'a', 'é', 'e\u0301', '类']
        for _ in range(1000):
            count = rng.randrange(257)
            uris = [rng.choice(keys) for _ in range(count)]
            classes = [rng.choice(keys) for _ in range(count)]
            skipped = [rng.randrange(4) == 0 for _ in range(count)]
            old = legacy_duplicates(uris, skipped), legacy_duplicates(classes, skipped)
            new = dense_duplicates(uris, skipped), dense_duplicates(classes, skipped)
            self.assertEqual([[name for name, indexes in zip(['uri', 'class'], old) if i in indexes] for i in range(count)],
                             [[name for name, indexes in zip(['uri', 'class'], new) if i in indexes] for i in range(count)])

    def test_exhaustive_owner_subsets_and_restoration(self):
        helpers = tuple(Helper(str(i), 'helper\n' + str(i)) for i in range(3))
        slots = [Slot('a', helpers[:1]), Slot('b', helpers[1:]), Slot('c', helpers + helpers)]
        subsets = [[s for s, keep in zip(slots, selection) if keep] for selection in product([False, True], repeat=3)]
        for before in subsets:
            for after in subsets:
                for phase in [before, after, after, before]:
                    self.assertEqual(old_publish(phase), new_publish(phase))

    def test_seeded_long_lived_ownership(self):
        rng = random.Random(77439)
        helpers = tuple(Helper(str(i), ('\n' if i % 3 == 0 else '') + str(i)) for i in range(17))
        slots = [Slot('body' + str(i), tuple(rng.choice(helpers) for _ in range(rng.randrange(20)))) for i in range(24)]
        for _ in range(10000):
            current = [s for s in slots if rng.randrange(3)]
            self.assertEqual(old_publish(current), new_publish(current))
            first = new_publish(current)
            snapshots = [s.previous for s in current]
            second = new_publish(current)
            self.assertTrue(all(a is b for a, b in zip(first, second)))
            self.assertTrue(all(a is s.previous for a, s in zip(snapshots, current)))

    def test_non_adjacent_duplicate_helpers_first_occurrence(self):
        first, last = Helper('a', 'first'), Helper('a', 'last')
        slots = [Slot('body', (first, Helper('b', ''), last))]
        self.assertEqual(['body\nfirst\n'], new_publish(slots))
        self.assertEqual(old_publish(slots), new_publish(slots))


if __name__ == '__main__':
    unittest.main()
