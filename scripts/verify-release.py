#!/usr/bin/env python3
"""Verify all release bytes, package identities and the expected tag commit before publishing."""
import argparse
from pathlib import Path

from release_common import load_manifest, verify_candidate


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--commit', required=True)
    args = parser.parse_args()
    inventory = verify_candidate(args.directory.resolve(), load_manifest(), args.version, args.commit)
    print(f"Verified {len(inventory['packages'])} packages for {args.version} at {args.commit}.")


if __name__ == '__main__':
    main()
