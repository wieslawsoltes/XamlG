#!/usr/bin/env python3
"""Materialize one hash-pinned patch and upload Git objects, without moving refs."""
from __future__ import annotations

import base64
import bz2
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

REPOSITORY = "wieslawsoltes/XamlG"
BASE_COMMIT = "9273227236612fe3f3914a5327a1a760a558ea8a"
BASE_TREE = "bbb64416e02a0fa917f3d6db21f56ac5adc9ef40"
TARGET_TREE = "9760ab0d6c030d94130e813f5b1e6666a68f1eeb"
PATCH_SHA = "0849b12152b21e0587b5a0518b18e98b00fd39c8c63f72f6a87d28eaddc3da01"
PAYLOAD_SHA = "d4425d59141fb0c0ba8b8c55c72df8d129dbf3e1f561694a19b8d016ed0dc178"
PATCH_LENGTH = 272311
FILE_COUNT = 89


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def git(directory: Path, *arguments: str) -> bytes:
    return subprocess.run(
        ["git", "-C", str(directory), *arguments],
        check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=90,
    ).stdout


def read_patch(directory: Path) -> bytes:
    expected = [f"patch-{index:02}.b64" for index in range(9)]
    require(sorted(path.name for path in directory.glob("patch-*.b64")) == expected,
            "Unexpected transfer chunk set.")
    chunks = [directory.joinpath(name).read_text(encoding="ascii").strip()
              for name in expected]
    require([len(chunk) for chunk in chunks] == [8192] * 8 + [5416],
            "Unexpected encoded chunk lengths.")
    payload = base64.b64decode("".join(chunks), validate=True)
    require(hashlib.sha256(payload).hexdigest() == PAYLOAD_SHA,
            "Compressed payload digest mismatch.")
    decoder = bz2.BZ2Decompressor()
    patch = decoder.decompress(payload, max_length=PATCH_LENGTH + 1)
    require(decoder.eof and not decoder.unused_data and len(patch) == PATCH_LENGTH,
            "Invalid, oversized, or trailing compressed data.")
    require(hashlib.sha256(patch).hexdigest() == PATCH_SHA, "Patch digest mismatch.")
    return patch


def post(endpoint: str, body: dict, token: str) -> dict:
    # Fixed repository and object-only endpoint allow-list: this script never updates refs.
    require(endpoint in ("git/blobs", "git/trees"), "Only Git object writes are permitted.")
    data = json.dumps(body, separators=(",", ":")).encode("utf-8")
    address = f"https://api.github.com/repos/{REPOSITORY}/{endpoint}"
    for attempt in range(5):
        request = Request(address, data=data, method="POST", headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "Content-Type": "application/json",
            "User-Agent": "XamlG-Verified-Snapshot-Import",
        })
        try:
            with urlopen(request, timeout=60) as response:
                return json.load(response)
        except HTTPError as error:
            if error.code in (429, 500, 502, 503, 504) and attempt < 4:
                error.close()
                time.sleep(2 ** attempt)
                continue
            status = error.code
            error.close()
            raise RuntimeError(f"Git object upload failed: HTTP {status} at {endpoint}.") from None
        except (URLError, TimeoutError):
            if attempt == 4:
                raise RuntimeError(f"Git object upload exhausted retries at {endpoint}.") from None
            time.sleep(2 ** attempt)
    raise RuntimeError("Git object upload did not return.")


def main() -> None:
    require(os.environ.get("GITHUB_REPOSITORY") == REPOSITORY, "Unexpected repository.")
    require(os.environ.get("GITHUB_REF") == "refs/heads/ops/import-authoring-9760ab0d",
            "This importer is restricted to its temporary branch.")
    token = os.environ["GH_TOKEN"]
    source = Path(os.environ["GITHUB_WORKSPACE"]).resolve()
    transfer = source / ".bootstrap" / "authoring-9760"
    patch = read_patch(transfer)
    require(git(source, "rev-parse", BASE_COMMIT + "^{tree}").decode().strip() == BASE_TREE,
            "Upstream commit/tree mismatch.")
    temporary = Path(tempfile.mkdtemp(prefix="xamlg-authoring-", dir=os.environ["RUNNER_TEMP"]))
    worktree = temporary / "source"
    patch_file = temporary / "authoring.patch"
    patch_file.write_bytes(patch)
    git(source, "worktree", "add", "--detach", str(worktree), BASE_COMMIT)
    git(worktree, "apply", "--check", "--index", str(patch_file))
    git(worktree, "apply", "--index", str(patch_file))
    actual = git(worktree, "write-tree").decode().strip()
    require(actual == TARGET_TREE, "Applied patch did not reproduce the verified source tree.")

    paths = [item.decode("utf-8") for item in git(
        worktree, "diff", "--cached", "--name-only", "--no-renames", "-z", BASE_COMMIT
    ).split(b"\0") if item]
    require(len(paths) == FILE_COUNT and len(set(paths)) == FILE_COUNT,
            "Unexpected changed-file count.")
    entries = {}
    for raw in git(worktree, "ls-files", "--stage", "-z").split(b"\0"):
        if not raw:
            continue
        metadata, raw_path = raw.split(b"\t", 1)
        mode, sha, stage = metadata.decode("ascii").split()
        require(stage == "0", "Unmerged index entry.")
        entries[raw_path.decode("utf-8")] = (mode, sha)

    def upload(path: str) -> dict:
        require(path in entries, "Deletion was not expected in this pinned patch.")
        require(not path.startswith("/") and ".." not in path.split("/"), "Invalid source path.")
        mode, sha = entries[path]
        require(mode in ("100644", "100755"), "Only regular source files may be uploaded.")
        blob = git(worktree, "cat-file", "blob", sha)
        require(len(blob) <= 2 * 1024 * 1024, "Source file exceeds import size limit.")
        result = post("git/blobs", {
            "content": base64.b64encode(blob).decode("ascii"), "encoding": "base64",
        }, token)
        require(result.get("sha") == sha, f"Remote blob digest mismatch: {path}.")
        return {"path": path, "mode": mode, "type": "blob", "sha": sha}

    with ThreadPoolExecutor(max_workers=4) as pool:
        uploaded = list(pool.map(upload, paths))
    result = post("git/trees", {"base_tree": BASE_TREE, "tree": uploaded}, token)
    require(result.get("sha") == TARGET_TREE, "Remote tree differs from the verified source.")
    evidence = {
        "repository": REPOSITORY, "base_commit": BASE_COMMIT, "base_tree": BASE_TREE,
        "target_tree": TARGET_TREE, "changed_files": FILE_COUNT, "patch_sha256": PATCH_SHA,
        "uploaded_objects_only": True, "source_code_executed": False,
    }
    evidence_file = Path(os.environ["RUNNER_TEMP"]) / "authoring-import-result.json"
    evidence_file.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(evidence, indent=2))
    # No branch/commit/tag is written here. The final source commit is published separately
    # through the authenticated GitHub connection after verification of this object hash.


if __name__ == "__main__":
    main()
