#!/usr/bin/env python3
"""Fail closed on incomplete/mismatched compiler evidence; timing noise is reported, not hidden by thresholds."""
import argparse
import json
import math
import re
from pathlib import Path

PROJECTS = {'Avalonia.Themes.Simple', 'Avalonia.Themes.Fluent', 'ControlCatalog'}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def identity(report, baseline, candidate):
    require(report.get('before_commit') == baseline, 'Unexpected baseline commit')
    require(report.get('after_commit') == candidate, 'Unexpected candidate commit')
    require(report.get('tracked_changes') == '', 'Measurements contain tracked working-tree edits')
    require(bool(report.get('sdk')), 'Missing SDK identity')


def verify(probe, catalog, baseline, candidate, identical_sources=False, workloads=None):
    for commit in (baseline, candidate):
        require(bool(re.fullmatch(r'[0-9a-f]{40}', commit)), 'Expected full commit identities')
    identity(probe, baseline, candidate)
    identity(catalog, baseline, candidate)
    require(probe['sdk'] == catalog['sdk'], 'Probe and catalog SDKs differ')
    pairs = probe.get('pairs', 0)
    require(type(pairs) is int and pairs >= 3, 'At least three probe pairs are required')
    runs = probe.get('runs', [])
    expected = {(variant, pair) for variant in ('before', 'after') for pair in range(1, pairs + 1)}
    require(len(runs) == len(expected) and {(r['variant'], r['pair']) for r in runs} == expected, 'Missing/duplicate probe pair')
    hashes = {r.get('semantic_sha256', '') for r in runs}
    require(len(hashes) == 1 and all(re.fullmatch(r'[0-9a-fA-F]{64}', value) for value in hashes), 'Semantic snapshots differ or are missing')
    require(probe.get('semantic_equivalence') is True, 'Semantic comparison was not successful')
    if workloads is not None:
        require(isinstance(workloads, list) and workloads and all(isinstance(name, str) and name.strip() == name and name for name in workloads),
                'Invalid workload manifest')
        require(len(set(workloads)) == len(workloads), 'Duplicate workload manifest entry')
    names = None
    operations = {}
    for run in runs:
        measurements = run.get('measurements', [])
        actual = {row['Name'] for row in measurements}
        require(actual and len(actual) == len(measurements), 'Missing/duplicate probe workload')
        if names is None:
            names = actual
        require(actual == names, 'Workload coverage differs between probe processes')
        if workloads is not None:
            require(actual == set(workloads), 'Workload coverage differs from the committed manifest')
        for row in measurements:
            require(type(row['Operations']) is int and row['Operations'] > 0, 'Invalid operation count')
            require(operations.setdefault(row['Name'], row['Operations']) == row['Operations'],
                    'Operation count differs between probe processes for ' + row['Name'])
            for field in ('Nanoseconds', 'AllocatedBytes'):
                values = row.get(field, [])
                require(len(values) == 5 and all(isinstance(value, (int, float)) and not isinstance(value, bool) and
                    math.isfinite(value) and (value > 0 if field == 'Nanoseconds' else value >= 0) for value in values),
                    'Invalid/incomplete samples for ' + row['Name'])
    iterations = catalog.get('iterations', 0)
    require(type(iterations) is int and iterations >= 3, 'At least three compiler pairs are required')
    projects = catalog.get('projects', [])
    require(len(projects) == len(PROJECTS) and {p['project'] for p in projects} == PROJECTS, 'Incomplete full-catalog coverage')
    full_expected = {(v, phase, i) for v in ('before', 'after') for phase in ('full', 'captured') for i in range(1, iterations + 1)}
    source_count = 0
    for project in projects:
        rows = project.get('runs', [])
        require(len(rows) == len(full_expected) and {(r['variant'], r['phase'], r['iteration']) for r in rows} == full_expected,
                'Missing/duplicate compiler pair for ' + project['project'])
        require(all(isinstance(row.get('wall_seconds'), (int, float)) and math.isfinite(row['wall_seconds']) and row['wall_seconds'] > 0 for row in rows),
                'Invalid compiler elapsed time')
        for variant in ('before', 'after'):
            source = project['sources'][variant]
            require(source.get('files', 0) > 0 and source['files'] == len(source['sha256']) and source.get('bytes', 0) > 0,
                    'Incomplete generated-source inventory')
            require(all(re.fullmatch(r'[0-9a-f]{64}', value) for value in source['sha256'].values()), 'Invalid generated-source digest')
        before, after = project['sources']['before'], project['sources']['after']
        if identical_sources:
            require(before == after, 'Generated C# changed under the byte-identical policy: ' + project['project'])
        source_count += after['files']
    return f'PASS: {len(names)} workloads, {pairs} probe pairs, {len(PROJECTS)} catalog projects, {iterations} compiler pairs, {source_count} generated files; semantic snapshots agree.'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--probe', type=Path, default=Path('artifacts/fast-path-comparison/results.json'))
    parser.add_argument('--catalog', type=Path, default=Path('artifacts/controlcatalog-comparison/results.json'))
    parser.add_argument('--baseline', required=True)
    parser.add_argument('--candidate', required=True)
    parser.add_argument('--require-identical-sources', action='store_true')
    parser.add_argument('--workload-manifest', type=Path, help='Require the exact committed JSON list of workload names')
    args = parser.parse_args()
    workloads = json.loads(args.workload_manifest.read_text()) if args.workload_manifest else None
    print(verify(json.loads(args.probe.read_text()), json.loads(args.catalog.read_text()),
                 args.baseline, args.candidate, args.require_identical_sources, workloads))


if __name__ == '__main__':
    main()
