#!/usr/bin/env python3
"""Independent checkpoint model, not execution of the C# implementation."""
import bisect
import itertools
import random
import unittest


# Raw token -> decoded UTF-16 code units. Literal surrogate units intentionally
# have ordinary edit boundaries; only one supplementary entity forbids a split.
TOKENS = {'a': 'a', ' ': ' ', '\t': '\t', '\r': '\r', '\n': '\n', '\r\n': '\r\n',
          '\ud800': '\ud800', '\udfff': '\udfff', '\0': '\0', '&amp;': '&',
          '&#x10000;': '\ud800\udc00', '&#13;': '\r'}


def reference(tokens, attribute):
    raw = ''.join(tokens)
    output, boundaries, position = [], [0], 0
    while position < len(raw):
        if raw[position] == '&':
            end = raw.index(';', position) + 1
            value = TOKENS[raw[position:end]]
            position = end
            output.extend(value)
            if len(value) == 2:
                boundaries.append(-1)
        elif raw[position] == '\r':
            position += 1
            if position < len(raw) and raw[position] == '\n':
                position += 1
            output.append(' ' if attribute else '\n')
        else:
            c = raw[position]
            output.append(' ' if attribute and c in '\t\n' else c)
            position += 1
        boundaries.append(position)
    return ''.join(output), boundaries


def checkpoint_model(raw, attribute):
    changes, output = [], []
    position = 0
    while position < len(raw):
        c = raw[position]
        if c == '&':
            end = raw.index(';', position) + 1
            value = TOKENS[raw[position:end]]
            if len(value) == 2:
                changes.append((len(output) + 1, -1))
            output.extend(value)
            position = end
            changes.append((len(output), position - len(output)))
        elif c == '\r':
            position += 1
            pair = position < len(raw) and raw[position] == '\n'
            position += pair
            output.append(' ' if attribute else '\n')
            if pair:
                changes.append((len(output), position - len(output)))
        else:
            output.append(' ' if attribute and c in '\t\n' else c)
            position += 1
    return ''.join(output), changes


def lookup(changes, position):
    index = bisect.bisect_right(changes, (position, float('inf'))) - 1
    if index < 0:
        return position
    offset = changes[index][1]
    return -1 if offset < 0 else position + offset


class Tests(unittest.TestCase):
    def check(self, tokens):
        for attribute in (False, True):
            text, boundaries = reference(tokens, attribute)
            actual, changes = checkpoint_model(''.join(tokens), attribute)
            self.assertEqual(text, actual)
            self.assertEqual(boundaries, [lookup(changes, p) for p in range(len(text) + 1)])
            self.assertTrue(all(a[0] < b[0] for a, b in zip(changes, changes[1:])))
            for i, (position, offset) in enumerate(changes):
                if offset < 0:
                    self.assertEqual(changes[i + 1][0], position + 1)
                    self.assertGreaterEqual(changes[i + 1][1], 0)

    def test_exhaustive_token_boundaries(self):
        for length in range(4):
            for tokens in itertools.product(TOKENS, repeat=length):
                self.check(tokens)

    def test_seeded_sparse_and_dense_documents(self):
        randomizer = random.Random(11713)
        keys = list(TOKENS)
        for _ in range(1000):
            self.check([randomizer.choice(keys) for _ in range(randomizer.randrange(1, 200))])

    def test_surrogate_entities_and_literal_surrogates(self):
        self.check(['\ud800', '&#x10000;', '\udfff'] * 128)

    def test_sparse_payload_does_not_scale_with_plain_run(self):
        for count in (128, 4096, 65536):
            text, changes = checkpoint_model('a' * count + '&amp;' + 'a' * count, True)
            self.assertEqual(changes, [(count + 1, 4)])
            self.assertLess(8 * len(changes), 4 * (len(text) + 1))

    def test_dense_publication_reconstruction(self):
        text, changes = checkpoint_model('&amp;&#x10000;' * 1024, True)
        self.assertGreaterEqual(2 * len(changes), len(text) + 1)
        self.check(['&amp;', '&#x10000;'] * 1024)

    def test_same_length_normalization_needs_no_changes(self):
        text, changes = checkpoint_model('a' * 1024 + '\t\r', True)
        self.assertEqual(text, 'a' * 1024 + '  ')
        self.assertEqual(changes, [])


if __name__ == '__main__':
    unittest.main()
