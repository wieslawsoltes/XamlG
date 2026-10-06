#!/usr/bin/env python3
"""Additional project-scoped fingerprint contracts. Retain the original unchanged timestamp test."""
import importlib.util
from pathlib import Path
import subprocess
import unittest

spec = importlib.util.spec_from_file_location('input_contracts', Path(__file__).with_name('test-msbuild-inputs.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class Fingerprints(module.Inputs):
    def test_project_relocation_does_not_invalidate_identical_inputs(self):
        first = self.run_project()[2]
        second = self.run_project()[2]
        self.assertEqual((first / 'obj/XamlG/inputs.cache').read_bytes(), (second / 'obj/XamlG/inputs.cache').read_bytes())

    def test_renaming_physical_input_changes_identity_even_with_the_same_link(self):
        a = self.run_project('<ItemGroup><XamlGSource Update="A.axaml" Link="Shared.axaml" /></ItemGroup>')[2]
        b = self.run_project('<ItemGroup><XamlGSource Update="B.axaml" Link="Shared.axaml" /></ItemGroup>', files=('B.axaml',))[2]
        self.assertNotEqual((a / 'obj/XamlG/inputs.cache').read_bytes(), (b / 'obj/XamlG/inputs.cache').read_bytes())

    def test_custom_generator_metadata_participates_in_fingerprint(self):
        a = self.run_project('<ItemGroup><XamlGSource Update="A.axaml" Marker="first" /></ItemGroup>')[2]
        b = self.run_project('<ItemGroup><XamlGSource Update="A.axaml" Marker="second" /></ItemGroup>')[2]
        self.assertNotEqual((a / 'obj/XamlG/inputs.cache').read_bytes(), (b / 'obj/XamlG/inputs.cache').read_bytes())

    def test_owned_metadata_is_case_insensitive(self):
        a = self.run_project('<ItemGroup><XamlGSource Update="A.axaml" xamlgcompile="false" /></ItemGroup>')
        self.assertEqual(a[0], ['A.axaml|A.axaml|false|'])

    def test_unchanged_relative_and_absolute_invocations_preserve_cache_bytes_and_timestamp(self):
        root = self.run_project()[2]
        cache = root / 'obj/XamlG/inputs.cache'
        before = (cache.read_bytes(), cache.stat().st_mtime_ns)
        for project in ('Input.proj', str((root / 'Input.proj').resolve())):
            result = subprocess.run(['dotnet', 'msbuild', project, '-nologo', '-v:q', '-t:Dump'], cwd=root, capture_output=True, text=True, timeout=90)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual(cache.read_bytes(), before[0], 'Input fingerprint changed between path spellings')
            self.assertEqual(cache.stat().st_mtime_ns, before[1], 'Identical input fingerprint was rewritten')


if __name__ == '__main__':
    suite = unittest.TestSuite(Fingerprints(name) for name in Fingerprints.__dict__ if name.startswith('test_'))
    raise SystemExit(not unittest.TextTestRunner(verbosity=2).run(suite).wasSuccessful())
