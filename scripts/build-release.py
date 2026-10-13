#!/usr/bin/env python3
"""Build the declared shipping packages, inspect their contents, and write a hashed release inventory."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

from release_common import ROOT, inspect_package, load_manifest, validate_version, verify_candidate


def run(*args):
    subprocess.run(args, cwd=ROOT, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--version', required=True)
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/release')
    parser.add_argument('--no-build', action='store_true')
    parser.add_argument('--allow-dirty', action='store_true', help='Build an unpublished local candidate from uncommitted changes.')
    args = parser.parse_args()
    try:
        validate_version(args.version)
    except ValueError as error:
        parser.error(str(error))
    manifest = load_manifest()
    dirty = bool(subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=normal'], cwd=ROOT, text=True).strip())
    if dirty and not args.allow_dirty:
        raise RuntimeError('Commit the source before creating a release, or use --allow-dirty for local validation only.')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    if any(output.iterdir()):
        raise RuntimeError('Release output must be an empty directory; existing artifacts will not be overwritten.')
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    properties = [f'-p:Version={args.version}', f'-p:PackageVersion={args.version}',
                  f'-p:RepositoryCommit={commit}', '-p:ContinuousIntegrationBuild=true', '-warnaserror']
    inventory = []
    for package in manifest:
        project = ROOT / package['project']
        if not project.is_file() or not project.resolve().is_relative_to(ROOT):
            raise RuntimeError('Invalid shipping project path.')
        command = ['dotnet', 'pack', str(project), '-c', 'Release', '-o', str(output), *properties]
        if args.no_build:
            command.append('--no-build')
        if package['id'] != 'XamlG.Generator' and 'command' not in package:
            command.extend(['--include-symbols', '-p:SymbolPackageFormat=snupkg'])
        run(*command)
        path = output / f"{package['id']}.{args.version}.nupkg"
        inventory.append(inspect_package(path, package, args.version, commit, {item['id'] for item in manifest}))
        if package['id'] != 'XamlG.Generator' and 'command' not in package:
            if not (output / f"{package['id']}.{args.version}.snupkg").is_file():
                raise RuntimeError('Missing portable symbols for ' + package['id'])
    if len(list(output.glob('*.nupkg'))) != len(manifest):
        raise RuntimeError('The output package set differs from the release manifest.')
    source = output / f'XamlG.{args.version}.source.tar.gz'
    run('git', 'archive', '--format=tar.gz', '-o', str(source), commit)
    (output / 'package-inventory.json').write_text(json.dumps({'sourceCommit': commit, 'sourceDirty': dirty, 'version': args.version, 'packages': inventory}, indent=2) + '\n')
    (output / 'release-notes.md').write_text(
        f'# XamlG {args.version}\n\nSource commit: `{commit}`.\n\n'
        + ('LOCAL VALIDATION ONLY: built with uncommitted changes; the source archive records the base commit. Do not publish.\n\n' if dirty else '') +
        'This development release contains the modular compiler, framework adapter, runtime, generator, tooling, project-system, workspace, language-server, intelligent-UI, automation, MCP and coding-agent libraries, official OpenAI/Anthropic/Gemini SDK adapters, and the xamlg/xamlg-lsp/xamlg-studio tools.\n\n'
        'See the included README and documentation for validated behavior and compatibility boundaries. '
        'The upstream baseline and XamlG compatibility suites are separate. Browser isolation protects editor origin access, not operating-system resource quotas.\n\n'
        'SHA256SUMS covers every attached package, symbol package, source archive, inventory and this file. '
        'The inventory records declared NuGet dependencies; it is not a vulnerability scan or a complete SBOM.\n')
    checksums = [f'{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}' for path in sorted(output.iterdir()) if path.is_file()]
    (output / 'SHA256SUMS').write_text('\n'.join(checksums) + '\n')
    verify_candidate(output, manifest, args.version, commit, allow_dirty=args.allow_dirty)
    print(f'Verified {len(inventory)} packages from {commit}; artifacts: {output}')


if __name__ == '__main__':
    main()
