"""Regression coverage for missing packages and unsafe release publication."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import sys
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))
from release_common import ROOT, REPOSITORY, inspect_package, load_manifest, validate_version, verify_candidate

spec = importlib.util.spec_from_file_location('release_version', SCRIPTS / 'resolve-release-version.py')
release_version = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release_version)
spec = importlib.util.spec_from_file_location('publish_release', SCRIPTS / 'publish-release.py')
publish_release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publish_release)


class VersionTests(unittest.TestCase):
    def test_stable_and_prerelease_versions(self):
        for version in ('1.2.3', '0.1.0-preview.2', '1.0.0-rc-test.0'):
            self.assertEqual(version, validate_version(version))

    def test_noncanonical_versions_are_rejected_before_packing(self):
        for version in ('v1.2.3', '01.2.3', '1.2', '1.2.3-preview.01', '1.2.3+build', '1.2.3-x..y', '../1.2.3'):
            with self.subTest(version=version), self.assertRaises(ValueError):
                validate_version(version)

    def test_publish_from_a_branch_is_an_error(self):
        with self.assertRaisesRegex(ValueError, 'existing v<version> tag'):
            release_version.resolve_version('refs/heads/main', 'main', '12', True, '0.1.0-alpha.1')
        self.assertEqual('0.1.0-ci.12', release_version.resolve_version('refs/pull/5/merge', '5/merge', '12', False, '0.1.0-alpha.1'))
        self.assertEqual('2.0.0-rc.1', release_version.resolve_version('refs/tags/v2.0.0-rc.1', 'v2.0.0-rc.1', '12', True, '0.1.0'))


class ManifestTests(unittest.TestCase):
    def test_readme_covers_the_shipping_manifest(self):
        packages = load_manifest()
        table = (ROOT / 'README.md').read_text().split('<!-- nuget-packages:start -->')[1].split('<!-- nuget-packages:end -->')[0]
        ids = re.findall(r'^\| \[(XamlG\.[^]]+)\]', table, re.MULTILINE)
        self.assertEqual([package['id'] for package in packages], ids)
        for package in packages:
            self.assertIn(f"nuget/vpre/{package['id']}.svg", table)
            self.assertIn(f"nuget/dt/{package['id']}.svg", table)

    def test_unlisted_project_system_is_caught_before_building(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for directory in ('src', 'tools'):
                for project in (ROOT / directory).glob('*/*.csproj'):
                    target = root / project.relative_to(ROOT)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(project, target)
            shutil.copyfile(ROOT / 'XamlG.slnx', root / 'XamlG.slnx')
            (root / 'eng').mkdir()
            packages = [package for package in load_manifest() if package['id'] != 'XamlG.ProjectSystem']
            (root / 'eng/release-manifest.json').write_text(json.dumps({'packages': packages}))
            with self.assertRaisesRegex(RuntimeError, 'unlisted=.*XamlG.ProjectSystem'):
                load_manifest(root)


class CandidateTests(unittest.TestCase):
    version = '1.0.0-preview.2'
    commit = 'a' * 40
    package = {'id': 'XamlG.Example', 'project': 'src/XamlG.Example/XamlG.Example.csproj'}

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.filename = f"XamlG.Example.{self.version}.nupkg"
        self.write_package()
        (self.root / f'XamlG.Example.{self.version}.snupkg').write_bytes(b'symbols')
        (self.root / f'XamlG.{self.version}.source.tar.gz').write_bytes(b'source')
        (self.root / 'release-notes.md').write_text(f'Source commit: `{self.commit}`')
        self.inventory = {'version': self.version, 'sourceCommit': self.commit, 'sourceDirty': False,
                          'packages': [inspect_package(self.root / self.filename, self.package, self.version, self.commit, {'XamlG.Example'})]}
        self.write_inventory()

    def write_package(self, dependency=None, entry=None):
        dependencies = '' if dependency is None else f'<dependencies><dependency id="{dependency[0]}" version="{dependency[1]}" /></dependencies>'
        with zipfile.ZipFile(self.root / self.filename, 'w') as archive:
            archive.writestr('XamlG.Example.nuspec', f'''<package><metadata>
                <id>XamlG.Example</id><version>{self.version}</version><license type="expression">MIT</license>
                <repository type="git" url="{REPOSITORY}" commit="{self.commit}" />
                <readme>README.md</readme><projectUrl>{REPOSITORY}</projectUrl><description>Example library.</description>
                {dependencies}</metadata></package>''')
            for name in ('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md'):
                archive.writestr(name, 'documentation')
            if entry:
                archive.writestr(entry, 'invalid entry')

    def write_inventory(self):
        (self.root / 'package-inventory.json').write_text(json.dumps(self.inventory))
        self.checksums()

    def checksums(self):
        (self.root / 'SHA256SUMS').write_text(''.join(
            f'{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n'
            for path in sorted(self.root.iterdir()) if path.name != 'SHA256SUMS'))

    def verify(self):
        return verify_candidate(self.root, [self.package], self.version, self.commit)

    def test_complete_candidate_is_accepted(self):
        self.assertEqual(self.inventory, self.verify())

    def test_missing_internal_dependency_is_rejected(self):
        self.write_package(('XamlG.ProjectSystem', self.version))
        with self.assertRaisesRegex(RuntimeError, 'missing from the release manifest'):
            inspect_package(self.root / self.filename, self.package, self.version, self.commit, {'XamlG.Example'})

    def test_internal_version_must_match_not_just_contain_candidate_version(self):
        self.write_package(('XamlG.Example', self.version + '0'))
        with self.assertRaisesRegex(RuntimeError, 'versions are not aligned'):
            inspect_package(self.root / self.filename, self.package, self.version, self.commit, {'XamlG.Example'})

    def test_changed_package_bytes_are_rejected(self):
        with (self.root / self.filename).open('ab') as stream:
            stream.write(b'changed')
        with self.assertRaisesRegex(RuntimeError, 'checksum mismatch'):
            self.verify()

    def test_symbols_cannot_be_omitted_even_with_updated_checksums(self):
        (self.root / f'XamlG.Example.{self.version}.snupkg').unlink()
        self.checksums()
        with self.assertRaisesRegex(RuntimeError, 'including symbols and source'):
            self.verify()

    def test_extra_package_cannot_be_published(self):
        (self.root / 'Unexpected.1.0.0.nupkg').write_bytes(b'not in the manifest')
        self.checksums()
        with self.assertRaisesRegex(RuntimeError, 'differ from the shipping manifest'):
            self.verify()

    def test_inventory_cannot_misrepresent_dependencies(self):
        self.inventory['packages'][0]['dependencies'].append({'id': 'Unknown', 'version': '1.0.0'})
        self.write_inventory()
        with self.assertRaisesRegex(RuntimeError, 'inventory differs'):
            self.verify()

    def test_dirty_and_wrong_revision_candidates_cannot_be_published(self):
        self.inventory['sourceCommit'] = 'b' * 40
        self.write_inventory()
        with self.assertRaisesRegex(RuntimeError, 'source identity'):
            self.verify()
        self.inventory['sourceCommit'] = self.commit
        self.inventory['sourceDirty'] = True
        self.write_inventory()
        with self.assertRaisesRegex(RuntimeError, 'clean, recorded source'):
            self.verify()

    def test_archive_traversal_is_rejected(self):
        self.write_package(entry='../outside')
        with self.assertRaisesRegex(RuntimeError, 'Unsafe package entry'):
            inspect_package(self.root / self.filename, self.package, self.version, self.commit, {'XamlG.Example'})

    def test_checksum_traversal_is_rejected_before_reading(self):
        with (self.root / 'SHA256SUMS').open('a') as stream:
            stream.write('a' * 64 + '  ../outside\n')
        with self.assertRaisesRegex(RuntimeError, 'checksummed filename'):
            self.verify()

    def publish(self, query, gh):
        with patch.dict(os.environ, {'GH_REPO': 'wieslawsoltes/XamlG', 'RELEASE_TAG': 'v' + self.version}), \
             patch.object(sys, 'argv', ['publish-release.py', '--directory', str(self.root)]), \
             patch.object(publish_release, 'load_manifest', return_value=[self.package]), \
             patch.object(publish_release.subprocess, 'check_output', return_value=self.commit + '\n'), \
             patch.object(publish_release.subprocess, 'run', return_value=query), \
             patch.object(publish_release, 'gh', side_effect=gh):
            publish_release.main()

    def test_existing_public_release_is_verified_without_mutation(self):
        query = subprocess.CompletedProcess([], 0, json.dumps({
            'isDraft': False, 'body': f'Source commit: `{self.commit}`', 'url': 'https://example.test/release'}), '')
        actions = []

        def gh(*args):
            actions.append(args[1])
            self.assertEqual('download', args[1], 'A published release must never be mutated.')
            directory = Path(args[args.index('--dir') + 1])
            for path in self.root.iterdir():
                shutil.copyfile(path, directory / path.name)

        self.publish(query, gh)
        self.assertEqual(['download'], actions)

    def test_failed_draft_upload_never_publishes_the_release(self):
        query = subprocess.CompletedProcess([], 1, '', 'release not found')
        actions = []

        def gh(*args):
            actions.append(args[1])
            if args[1] == 'upload':
                raise subprocess.CalledProcessError(1, ['gh', *args])

        with self.assertRaises(subprocess.CalledProcessError):
            self.publish(query, gh)
        self.assertEqual(['create', 'upload'], actions)

    def test_api_failure_is_not_mistaken_for_a_missing_release(self):
        query = subprocess.CompletedProcess([], 1, '', 'HTTP 403: permission denied')
        with self.assertRaisesRegex(RuntimeError, 'Cannot establish existing release state'):
            self.publish(query, lambda *args: self.fail('Must not mutate releases after a failed state lookup.'))

    def test_draft_for_another_commit_is_not_overwritten(self):
        query = subprocess.CompletedProcess([], 0, json.dumps({'isDraft': True, 'body': 'Source commit: `' + 'b' * 40 + '`'}), '')
        with self.assertRaisesRegex(RuntimeError, 'different or unrecorded source revision'):
            self.publish(query, lambda *args: self.fail('Must not overwrite a draft for another commit.'))


if __name__ == '__main__':
    unittest.main()
