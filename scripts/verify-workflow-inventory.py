#!/usr/bin/env python3
"""Keep the documented validation surface explicit; do not certify CI outcomes."""
import json
from pathlib import Path


def main():
    root = Path(__file__).resolve().parents[1]
    inventory = json.loads((root / 'eng/validation-workflows.json').read_text(encoding='utf-8'))
    if set(inventory) != {'pullRequest', 'diagnostic', 'deployment'}:
        raise RuntimeError('Unexpected workflow inventory categories.')
    entries = inventory['pullRequest'] + inventory['diagnostic'] + inventory['deployment']
    if len(entries) != len(set(entries)) or any(Path(name).name != name or not name.endswith('.yml') for name in entries):
        raise RuntimeError('Invalid or duplicate workflow filename.')
    directory = root / '.github/workflows'
    actual = {p.name for p in directory.iterdir() if p.is_file() and p.suffix in ('.yml', '.yaml')}
    if actual != set(entries):
        raise RuntimeError(f'Workflow inventory mismatch: unlisted={sorted(actual-set(entries))}, missing={sorted(set(entries)-actual)}')
    print(f'PASS: {len(inventory["pullRequest"])} PR workflows, {len(inventory["diagnostic"])} optional diagnostic workflow and {len(inventory["deployment"])} deployment workflow. This check does not replace their validation.')


if __name__ == '__main__':
    main()
