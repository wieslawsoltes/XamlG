"""Upload exact verified Git blobs only; never execute patched code or update references."""
import base64
from concurrent.futures import ThreadPoolExecutor
import gzip
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
from urllib.error import HTTPError
from urllib.request import Request, urlopen

REPO = 'wieslawsoltes/XamlG'
BASE = '65ac790b1a54547128e7ff3dfd4de7a421cbc9bd'
BASE_TREE = '9760ab0d6c030d94130e813f5b1e6666a68f1eeb'
TARGET = 'f56016f24ed07d7df734a2aed3175b1b3d82eeca'
DIGEST = 'fceba75e453de9e3255fc5393b80257a2b6ac7d204e2f932f596aedf076e3271'


def git(*arguments, cwd=None):
    return subprocess.check_output(['git', *arguments], cwd=cwd)


def upload(entry, worktree, token):
    data = git('cat-file', 'blob', entry['sha'], cwd=worktree)
    digest = hashlib.sha1(f'blob {len(data)}\0'.encode() + data).hexdigest()
    if digest != entry['sha']:
        raise RuntimeError('Local blob identity mismatch.')
    body = json.dumps({'content': base64.b64encode(data).decode('ascii'), 'encoding': 'base64'}).encode()
    for attempt in range(4):
        request = Request(f'https://api.github.com/repos/{REPO}/git/blobs', data=body, method='POST', headers={
            'Authorization': 'Bearer ' + token, 'Accept': 'application/vnd.github+json',
            'Content-Type': 'application/json', 'User-Agent': 'XamlG-verified-object-import'})
        try:
            with urlopen(request, timeout=60) as response:
                result = json.load(response)
            if result.get('sha') != entry['sha']:
                raise RuntimeError('Remote blob identity mismatch.')
            return entry
        except HTTPError as error:
            if error.code not in (429, 500, 502, 503, 504) or attempt == 3:
                raise RuntimeError(f'Blob upload failed with HTTP {error.code}.') from None
            time.sleep(2 ** attempt)
    raise RuntimeError('Upload retry limit reached.')


def main():
    if os.environ.get('GITHUB_REPOSITORY') != REPO:
        raise RuntimeError('Unexpected repository.')
    root = Path.cwd()
    if git('rev-parse', BASE + '^{tree}').decode().strip() != BASE_TREE:
        raise RuntimeError('Unexpected base tree.')
    parts = sorted((root / '.bootstrap/pr5-integration').glob('patch.*'))
    if len(parts) != 12:
        raise RuntimeError('Missing transfer parts.')
    packed = base64.b64decode(''.join(p.read_text() for p in parts), validate=True)
    if hashlib.sha256(packed).hexdigest() != DIGEST:
        raise RuntimeError('Compressed patch integrity check failed.')
    patch = gzip.decompress(packed)
    if len(patch) != 87570:
        raise RuntimeError('Unexpected patch size.')
    token = os.environ['GH_TOKEN']
    with tempfile.TemporaryDirectory(prefix='xamlg-import-', dir=os.environ['RUNNER_TEMP']) as folder:
        worktree = Path(folder) / 'worktree'
        patch_path = Path(folder) / 'source.patch'
        patch_path.write_bytes(patch)
        git('worktree', 'add', '--detach', str(worktree), BASE)
        try:
            git('apply', '--index', '--binary', '--unidiff-zero', str(patch_path), cwd=worktree)
            actual = git('write-tree', cwd=worktree).decode().strip()
            if actual != TARGET:
                raise RuntimeError('Patched tree is not the exact validated source snapshot.')
            paths = git('diff', '--cached', '--name-only', '-z', cwd=worktree).split(b'\0')
            paths = [p.decode('utf-8') for p in paths if p]
            if len(paths) != 33:
                raise RuntimeError('Unexpected changed-file count.')
            entries = []
            for path in paths:
                if path.startswith('/') or '..' in path.split('/'):
                    raise RuntimeError('Invalid repository path.')
                record = git('ls-tree', TARGET, '--', path, cwd=worktree).decode().strip()
                metadata, found = record.split('\t', 1)
                mode, kind, sha = metadata.split()
                if found != path or kind != 'blob' or mode not in ('100644', '100755'):
                    raise RuntimeError('Unexpected tree entry.')
                entries.append({'path': path, 'mode': mode, 'type': kind, 'sha': sha})
            with ThreadPoolExecutor(max_workers=4) as pool:
                uploaded = list(pool.map(lambda e: upload(e, worktree, token), entries))
            output = root / 'artifacts/import'
            output.mkdir(parents=True, exist_ok=True)
            (output / 'manifest.json').write_text(json.dumps({'base': BASE, 'tree': TARGET, 'entries': uploaded}, indent=2))
            print(f'Verified and uploaded {len(uploaded)} exact blobs for tree {TARGET}. No refs changed.')
        finally:
            # Only the worktree created inside this private temporary directory is removed.
            git('worktree', 'remove', '--force', str(worktree))


if __name__ == '__main__':
    main()
