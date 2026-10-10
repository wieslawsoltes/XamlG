#!/usr/bin/env python3
import copy
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('gate', Path(__file__).with_name('verify-compiler-performance.py'))
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)
BASE, HEAD = 'a' * 40, 'b' * 40


def reports():
    common = dict(before_commit=BASE, after_commit=HEAD, tracked_changes='', sdk='test-sdk')
    probe = dict(common, pairs=3, semantic_equivalence=True, runs=[dict(variant=v, pair=i, semantic_sha256='C' * 64,
        measurements=[dict(Name='work', Operations=1, Nanoseconds=[1.0] * 5, AllocatedBytes=[0.0] * 5)])
        for v in ('before', 'after') for i in range(1, 4)])
    source = dict(files=1, bytes=10, sha256={'one.g.cs': 'd' * 64})
    catalog = dict(common, iterations=3, projects=[dict(project=name, sources=dict(before=copy.deepcopy(source), after=copy.deepcopy(source)),
        runs=[dict(variant=v, phase=p, iteration=i, wall_seconds=1.0) for v in ('before', 'after')
              for p in ('full', 'captured') for i in range(1, 4)]) for name in sorted(gate.PROJECTS)])
    return probe, catalog


class Gates(unittest.TestCase):
    def test_complete_evidence_and_zero_allocations(self):
        self.assertTrue(gate.verify(*reports(), BASE, HEAD, True).startswith('PASS:'))

    def test_incomplete_or_duplicate_probe_pairs(self):
        for duplicate in (False, True):
            probe, catalog = reports()
            if duplicate: probe['runs'][-1] = probe['runs'][0]
            else: probe['runs'].pop()
            with self.assertRaises(ValueError): gate.verify(probe, catalog, BASE, HEAD)

    def test_semantic_mismatch(self):
        probe, catalog = reports(); probe['runs'][0]['semantic_sha256'] = 'e' * 64
        with self.assertRaises(ValueError): gate.verify(probe, catalog, BASE, HEAD)

    def test_missing_or_invalid_sample(self):
        for values in ([1.0] * 4, [float('nan')] * 5, [-1] * 5):
            probe, catalog = reports(); probe['runs'][0]['measurements'][0]['Nanoseconds'] = values
            with self.assertRaises(ValueError): gate.verify(probe, catalog, BASE, HEAD)

    def test_workload_coverage_mismatch(self):
        probe, catalog = reports(); probe['runs'][0]['measurements'][0]['Name'] = 'different'
        with self.assertRaises(ValueError): gate.verify(probe, catalog, BASE, HEAD)

    def test_missing_catalog_project_or_pair(self):
        for missing_project in (False, True):
            probe, catalog = reports()
            if missing_project: catalog['projects'].pop()
            else: catalog['projects'][0]['runs'].pop()
            with self.assertRaises(ValueError): gate.verify(probe, catalog, BASE, HEAD)

    def test_wrong_commit_dirty_sources_and_sdk_mismatch(self):
        for field, value in [('before_commit', HEAD), ('after_commit', BASE), ('tracked_changes', ' M file.cs'), ('sdk', 'other')]:
            probe, catalog = reports(); catalog[field] = value
            with self.assertRaises(ValueError): gate.verify(probe, catalog, BASE, HEAD)

    def test_byte_policy_is_explicit_and_semantics_are_always_required(self):
        probe, catalog = reports(); catalog['projects'][0]['sources']['after']['sha256']['one.g.cs'] = 'f' * 64
        with self.assertRaises(ValueError): gate.verify(probe, catalog, BASE, HEAD, True)
        self.assertTrue(gate.verify(probe, catalog, BASE, HEAD, False).startswith('PASS:'))
        probe['semantic_equivalence'] = False
        with self.assertRaises(ValueError): gate.verify(probe, catalog, BASE, HEAD, False)


    def test_exact_manifest_accepts_complete_coverage(self):
        self.assertTrue(gate.verify(*reports(), BASE, HEAD, True, ['work']).startswith('PASS:'))

    def test_workload_missing_from_every_process_is_not_silently_accepted(self):
        # All six runs agree, but the same omitted benchmark still invalidates them.
        with self.assertRaisesRegex(ValueError, 'committed manifest'):
            gate.verify(*reports(), BASE, HEAD, True, ['work', 'omitted-from-all-runs'])

    def test_extra_workload_requires_an_explicit_manifest_update(self):
        probe, catalog = reports()
        for run in probe['runs']:
            extra = copy.deepcopy(run['measurements'][0]); extra['Name'] = 'extra'
            run['measurements'].append(extra)
        with self.assertRaisesRegex(ValueError, 'committed manifest'):
            gate.verify(probe, catalog, BASE, HEAD, True, ['work'])

    def test_invalid_or_duplicate_workload_manifest(self):
        for manifest in ([], ['work', 'work'], ['work', ''], ['work', 123], [' work '], 'work', {'work': True}):
            with self.subTest(manifest=manifest), self.assertRaises(ValueError):
                gate.verify(*reports(), BASE, HEAD, True, manifest)

    def test_operation_count_must_match_in_all_processes_and_revisions(self):
        for indexes in ([0], [0, 1, 2], [3, 4, 5]):
            probe, catalog = reports()
            for index in indexes:
                probe['runs'][index]['measurements'][0]['Operations'] = 2
            with self.assertRaisesRegex(ValueError, 'Operation count differs'):
                gate.verify(probe, catalog, BASE, HEAD)

    def test_boolean_operation_count_is_not_an_integer_sample(self):
        probe, catalog = reports(); probe['runs'][0]['measurements'][0]['Operations'] = True
        with self.assertRaisesRegex(ValueError, 'Invalid operation count'):
            gate.verify(probe, catalog, BASE, HEAD)

if __name__ == '__main__':
    unittest.main()
