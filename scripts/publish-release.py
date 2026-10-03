#!/usr/bin/env python3
"""Publish a verified candidate via a draft; never replace different assets in a public release."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile


def gh(*args, capture=False):
    return subprocess.run(['gh', *args], check=True, text=True, capture_output=capture)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, default=Path('release'))
    args = parser.parse_args()
    root = args.directory.resolve()
    repository = os.environ['GH_REPO']
    tag = os.environ['RELEASE_TAG']
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository) or not re.fullmatch(r'v[0-9A-Za-z.-]+', tag):
        raise RuntimeError('Invalid repository or version tag.')
    inventory = json.loads((root / 'package-inventory.json').read_text())
    if tag != 'v' + inventory['version'] or not re.fullmatch(r'[0-9a-f]{40}', inventory['sourceCommit']):
        raise RuntimeError('Candidate version/source identity does not match the release tag.')
    marker = 'Source commit: `' + inventory['sourceCommit'] + '`'
    sums = (root / 'SHA256SUMS').read_text()
    expected_files = {'SHA256SUMS'}
    for line in sums.splitlines():
        digest, name = line.split('  ', 1)
        if Path(name).name != name or '\\' in name or name in expected_files:
            raise RuntimeError('Invalid or duplicated checksummed filename.')
        expected_files.add(name)
        if not re.fullmatch(r'[0-9a-f]{64}', digest) or hashlib.sha256((root / name).read_bytes()).hexdigest() != digest:
            raise RuntimeError('Release checksum mismatch: ' + name)
    if {p.name for p in root.iterdir()} != expected_files:
        raise RuntimeError('Candidate contains files not covered by SHA256SUMS.')
    query = subprocess.run(['gh', 'release', 'view', tag, '--repo', repository, '--json', 'isDraft,body,url'], capture_output=True, text=True)
    if query.returncode == 0:
        existing = json.loads(query.stdout)
        if marker not in existing['body']:
            raise RuntimeError('The existing release belongs to a different or unrecorded source revision.')
        if not existing['isDraft']:
            with tempfile.TemporaryDirectory() as temporary:
                gh('release', 'download', tag, '--repo', repository, '--pattern', 'SHA256SUMS', '--dir', temporary)
                if (Path(temporary) / 'SHA256SUMS').read_text() != sums:
                    raise RuntimeError('A published release is immutable: its artifacts differ from this candidate.')
            print('Existing public release matches the candidate:', existing['url'])
            return
    else:
        if 'release not found' not in query.stderr.lower() and 'http 404' not in query.stderr.lower():
            raise RuntimeError('Cannot establish existing release state: ' + query.stderr.strip())
        gh('release', 'create', tag, '--repo', repository, '--verify-tag', '--draft', '--title', 'XamlG ' + tag,
           '--notes-file', str(root / 'release-notes.md'))
    # Draft-only uploads are resumable. Public release assets are never clobbered.
    gh('release', 'upload', tag, '--repo', repository, '--clobber', *[str(root / name) for name in sorted(expected_files)])
    gh('release', 'edit', tag, '--repo', repository, '--draft=false',
       '--prerelease=' + str('-' in inventory['version']).lower())
    print('Published tested release:', tag, 'at source', inventory['sourceCommit'])


if __name__ == '__main__':
    main()
