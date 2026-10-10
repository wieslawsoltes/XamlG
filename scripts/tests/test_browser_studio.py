import argparse
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('browser_studio', Path(__file__).resolve().parents[1] / 'test-browser-studio.py')
launcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(launcher)

class ShardParsingTests(unittest.TestCase):
    def test_valid_shards(self):
        for value in ['1/1', '1/8', '8/8', '64/64']:
            with self.subTest(value=value): self.assertEqual(value, launcher.parse_shard(value))

    def test_invalid_and_unbounded_shards(self):
        for value in ['', '0/8', '9/8', '1/0', '1/65', '01/8', '1', '1/8 --grep=x', '../8', '１/８', '1/' + '9' * 1000]:
            with self.subTest(value=value), self.assertRaises(argparse.ArgumentTypeError): launcher.parse_shard(value)

if __name__ == '__main__': unittest.main()
