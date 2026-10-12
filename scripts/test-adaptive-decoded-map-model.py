#!/usr/bin/env python3
"""Check adaptive publication math against an independent per-character decoder.
This is a model, not execution of the C# implementation.
"""
import importlib.util
from pathlib import Path
import random
import unittest

spec = importlib.util.spec_from_file_location('reference_map', Path(__file__).with_name('test-decoded-source-map-model.py'))
reference = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reference)


def pack(boundaries):
    changes = []
    offset = 0
    for position, raw in enumerate(boundaries):
        if raw < 0:
            changes.append((position, -1))
        elif raw - position != offset:
            offset = raw - position
            changes.append((position, offset))
    return changes


def fast_lookup(changes, position):
    if position >= changes[-1][0]:
        return position + changes[-1][1]
    if position < changes[0][0]:
        return position
    low, high = 0, len(changes) - 1
    while low < high:
        middle = low + (high - low) // 2
        if changes[middle][0] <= position:
            low = middle + 1
        else:
            high = middle
    if not low:
        return position
    offset = changes[low - 1][1]
    return -1 if offset < 0 else position + offset


class Tests(unittest.TestCase):
    def check(self, tokens):
        for attribute in (False, True):
            text, dense = reference.reference(tokens, attribute)
            checkpoints = pack(dense)
            self.assertEqual((text, checkpoints), reference.checkpoint_model(''.join(tokens), attribute))
            if not checkpoints:
                self.assertEqual(dense, list(range(len(text) + 1)))
                continue
            self.assertEqual(dense, [fast_lookup(checkpoints, i) for i in range(len(dense))])
            # Promotion is legal only after a complete token, never an entity's
            # forbidden midpoint. Reconstruct each sampled prefix independently.
            candidates = [i for i, c in enumerate(checkpoints, 1) if i >= 32 and c[1] >= 0 and 2 * i >= c[0] + 1]
            if candidates:
                count = candidates[0]
                endpoint = checkpoints[count - 1][0]
                prefix = [reference.lookup(checkpoints[:count], i) for i in range(endpoint + 1)]
                self.assertEqual(dense[:endpoint + 1], prefix)
                # The final ordinary tail has the last checkpoint's offset and
                # contributes no entries to the compact representation.
                tail_start, tail_offset = checkpoints[-1]
                temporary = prefix + dense[endpoint + 1:tail_start + 1]
                self.assertEqual(checkpoints, pack(temporary))
                restored = temporary + [i + tail_offset for i in range(tail_start + 1, len(dense))]
                self.assertEqual(dense, restored)
                chosen_payload = min(8 * len(checkpoints), 4 * len(dense))
                self.assertLessEqual(chosen_payload, 4 * len(dense))

    def test_density_boundaries_and_omitted_tails(self):
        for count in (31, 32, 33, 63, 64, 65, 129):
            for tail in (0, 1, 4096):
                self.check(['&amp;', '&#x10000;', '\r\n'] * count + ['x' * tail])
                self.check(['x' * tail] + ['&amp;', '&#x10000;', '\r\n'] * count)

    def test_seeded_alternating_density(self):
        rng = random.Random(41671)
        for _ in range(2000):
            tokens = []
            for _ in range(rng.randrange(1, 9)):
                tokens += [rng.choice(['&amp;', '&#x10000;', '\r\n', '\t', '\r', '\ud800'])] * rng.randrange(1, 96)
                tokens.append('x' * rng.randrange(0, 512))
            self.check(tokens)


if __name__ == '__main__':
    unittest.main()
