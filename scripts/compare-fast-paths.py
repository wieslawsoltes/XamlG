#!/usr/bin/env python3
"""Compare the same public-API probe against two isolated XamlG revisions; not an XamlX build comparison."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import statistics
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
PROBE = Path('tools/XamlG.PerformanceProbe')


def capture(*args):
    return subprocess.check_output(args, cwd=ROOT, text=True).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline-ref', required=True)
    parser.add_argument('--pairs', type=int, default=3)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/fast-path-comparison')
    parser.add_argument('--allow-semantic-changes', action='store_true', help='Report, rather than reject, intentional changes in typed syntax/metadata snapshots.')
    args = parser.parse_args()
    if args.pairs < 3 or args.pairs > 20:
        parser.error('Use 3-20 alternating pairs.')
    if args.baseline_ref.startswith('-'):
        parser.error('The baseline must be a Git reference, not an option.')
    baseline = capture('git', 'rev-parse', '--verify', args.baseline_ref + '^{commit}')
    if not re.fullmatch('[0-9a-f]{40}', baseline):
        parser.error('The baseline did not resolve to a full commit SHA.')
    output = args.output.resolve()
    if output.exists() and any(output.iterdir()):
        parser.error('Use an empty output directory.')
    output.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ, DOTNET_TieredCompilation='0', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    report = {
        'before_commit': baseline, 'after_commit': capture('git', 'rev-parse', 'HEAD'),
        'sdk': capture(args.dotnet, '--version'), 'platform': platform.platform(), 'pairs': args.pairs,
        'tracked_changes': capture('git', 'status', '--porcelain', '--untracked-files=no'),
        'harness_sha256': {str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest()
                           for path in sorted((ROOT / PROBE).glob('*')) if path.is_file()},
        'method': 'Same probe compiled against each revision in isolated output directories. '
                  'Alternating AB/BA fresh-process pairs, three warmup calls and five batches per case. '
                  'Tiered compilation disabled for both variants. Input creation, snapshots, build and startup excluded. '
                  'Allocations use GC.GetAllocatedBytesForCurrentThread. Localized microbenchmarks, not end-to-end XamlX comparisons.',
        'runs': []
    }
    with tempfile.TemporaryDirectory(prefix='xamlg-fast-paths-') as temporary:
        checkout = Path(temporary) / 'before'
        subprocess.run(['git', 'worktree', 'add', '--detach', str(checkout), baseline], cwd=ROOT, check=True)
        try:
            # Use precisely the candidate harness for both variants, even when it
            # did not yet exist at the baseline. No compiler/runtime files are copied.
            shutil.copytree(ROOT / PROBE, checkout / PROBE, dirs_exist_ok=True,
                            ignore=shutil.ignore_patterns('bin', 'obj'))
            assemblies = {}
            for variant, root in [('before', checkout), ('after', ROOT)]:
                with (output / (variant + '-build.log')).open('w') as log:
                    subprocess.run([args.dotnet, 'build', str(root / PROBE), '-c', 'Release', '-warnaserror',
                                    '-p:NuGetAudit=false', '-p:UseSharedCompilation=false', '-m:1'],
                                   cwd=root, env=environment, stdout=log, stderr=subprocess.STDOUT, check=True)
                assemblies[variant] = root / PROBE / 'bin/Release/net10.0/XamlG.PerformanceProbe.dll'
            for pair in range(args.pairs):
                for variant in (('before', 'after') if pair % 2 == 0 else ('after', 'before')):
                    path = output / f'{variant}-{pair + 1}.json'
                    subprocess.run([args.dotnet, str(assemblies[variant]), str(path)], cwd=ROOT, env=environment, check=True)
                    data = json.loads(path.read_text())
                    report['runs'].append({'variant': variant, 'pair': pair + 1, **data})
                    print(f'Completed {variant} fast-path sample {pair + 1}; semantic SHA256 {data["semantic_sha256"]}', flush=True)
                    (output / 'results.json').write_text(json.dumps(report, indent=2) + '\n')
        finally:
            subprocess.run(['git', 'worktree', 'remove', '--force', str(checkout)], cwd=ROOT, check=True)
    hashes = {run['semantic_sha256'] for run in report['runs']}
    report['semantic_equivalence'] = len(hashes) == 1
    names = [row['Name'] for row in report['runs'][0]['measurements']]
    summary = []
    for name in names:
        row = {'name': name}
        for variant in ('before', 'after'):
            entries = [measurement for run in report['runs'] if run['variant'] == variant
                       for measurement in run['measurements'] if measurement['Name'] == name]
            row[variant] = {key: statistics.median(value for entry in entries for value in entry[key])
                            for key in ('Nanoseconds', 'AllocatedBytes')}
        row['time_ratio'] = row['after']['Nanoseconds'] / row['before']['Nanoseconds']
        row['allocation_ratio'] = row['after']['AllocatedBytes'] / row['before']['AllocatedBytes']
        summary.append(row)
    report['summary'] = summary
    lines = ['| Workload | Time before (us) | Time after (us) | After/before | Bytes before | Bytes after |',
             '| --- | ---: | ---: | ---: | ---: | ---: |']
    for row in summary:
        lines.append(f'| {row["name"]} | {row["before"]["Nanoseconds"] / 1000:.3f} | '
                     f'{row["after"]["Nanoseconds"] / 1000:.3f} | {row["time_ratio"]:.3f} | '
                     f'{row["before"]["AllocatedBytes"]:.0f} | {row["after"]["AllocatedBytes"]:.0f} |')
    lines += ['', f'Typed syntax/diagnostic/metadata snapshot equivalence: {report["semantic_equivalence"]}.', '', report['method']]
    text = '\n'.join(lines) + '\n'
    (output / 'results.json').write_text(json.dumps(report, indent=2) + '\n')
    (output / 'summary.md').write_text(text)
    print(text)
    if not report['semantic_equivalence'] and not args.allow_semantic_changes:
        raise RuntimeError('The baseline and candidate have different semantic snapshots; inspect the change before comparing timings.')


if __name__ == '__main__':
    main()
