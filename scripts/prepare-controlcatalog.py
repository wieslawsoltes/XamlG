#!/usr/bin/env python3
"""Prepare pinned Avalonia dependencies and verify the complete catalog/theme imports."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "samples/controlcatalog-upstream.json"


def git(directory, *args):
    return subprocess.check_output(["git", "-C", str(directory), *args], text=True).strip()


def verify(manifest, upstream):
    revision = git(upstream, "rev-parse", "HEAD")
    if revision != manifest["revision"]:
        raise RuntimeError(f"Expected pinned Avalonia {manifest['revision']}, found {revision}")
    if git(upstream, "status", "--porcelain", "--untracked-files=no"):
        raise RuntimeError("Pinned Avalonia sources contain tracked modifications")
    submodules = git(upstream, "submodule", "status", "--recursive")
    if any(line.startswith(("-", "+", "U")) for line in submodules.splitlines()):
        raise RuntimeError("Avalonia submodules must match their pinned revisions")
    original_paths = set(git(upstream, "ls-files", "--", *manifest["directories"]).splitlines())
    imported_paths = {item["source"] for item in manifest["files"]}
    if missing := original_paths - imported_paths:
        raise RuntimeError("Missing imported upstream files: " + repr(sorted(missing)))
    adaptations = manifest["adaptations"]
    seen = set()
    for item in manifest["files"]:
        path = item["path"]
        if path in seen:
            raise RuntimeError("Duplicate imported path: " + path)
        seen.add(path)
        source = upstream / item["source"]
        if hashlib.sha256(source.read_bytes()).hexdigest() != item["sha256"]:
            raise RuntimeError("Upstream content does not match the recorded SHA-256: " + item["source"])
        copy = ROOT / path
        if not copy.is_file():
            raise RuntimeError("Missing ported file: " + path)
        changed = hashlib.sha256(copy.read_bytes()).hexdigest() != item["sha256"]
        if changed and not adaptations.get(path):
            raise RuntimeError("Undocumented adaptation: " + path)
        if not changed and path in adaptations:
            raise RuntimeError("Stale adaptation: " + path)
    if set(adaptations) - seen:
        raise RuntimeError("Adaptations reference files outside the import inventory")
    classes = []
    for item in manifest["files"]:
        if item["path"].startswith("samples/ControlCatalog/") and Path(item["path"]).suffix in (".xaml", ".axaml", ".paml"):
            name = ET.parse(ROOT / item["path"]).getroot().get("{http://schemas.microsoft.com/winfx/2006/xaml}Class")
            if name:
                classes.append(name)
    inventory = json.loads((ROOT / "samples/ControlCatalog.Tests/components.json").read_text())
    if sorted(classes) != inventory:
        raise RuntimeError("Component validation inventory must cover every imported x:Class")
    print(f"Verified {len(seen)} imported files, {len(adaptations)} documented adaptations, Avalonia {revision}.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--upstream", type=Path, default=ROOT / "artifacts/controlcatalog-upstream")
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()
    manifest = json.loads(MANIFEST.read_text())
    upstream = args.upstream.resolve()
    if not upstream.exists():
        if args.verify_only:
            raise RuntimeError("Missing pinned source checkout: " + str(upstream))
        upstream.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run(["git", "init", str(upstream)], check=True)
        git(upstream, "remote", "add", "origin", manifest["repository"])
        git(upstream, "fetch", "--depth=1", "origin", manifest["revision"])
        git(upstream, "checkout", "--detach", manifest["revision"])
    if not args.verify_only:
        git(upstream, "submodule", "update", "--init", "--recursive", "--depth=1")
    verify(manifest, upstream)


if __name__ == "__main__":
    main()
