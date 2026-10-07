#!/usr/bin/env python3
"""Build the declared shipping packages, inspect their contents, and write a hashed release inventory."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = re.compile(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?')
NAMESPACE = {'n': 'http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd'}


def run(*args):
    subprocess.run(args, cwd=ROOT, check=True)


def local_name(element):
    return element.tag.rsplit('}', 1)[-1]


def inspect_package(path, package, version, commit):
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if len(set(names)) != len(names):
            raise RuntimeError(f'Duplicate ZIP entries in {path.name}')
        for name in names:
            entry = PurePosixPath(name)
            if entry.is_absolute() or '..' in entry.parts or '\\' in name:
                raise RuntimeError(f'Unsafe package entry: {name}')
        specs = [name for name in names if name.endswith('.nuspec')]
        if len(specs) != 1:
            raise RuntimeError('A package must contain exactly one nuspec.')
        metadata = next(e for e in ET.fromstring(archive.read(specs[0])) if local_name(e) == 'metadata')
        values = {local_name(e): e for e in metadata}
        if values['id'].text != package['id'] or values['version'].text != version:
            raise RuntimeError(f'Package identity mismatch: {path.name}')
        if values['license'].text != 'MIT':
            raise RuntimeError(f'Unexpected license in {path.name}')
        repository = values.get('repository')
        if repository is None or repository.get('commit') != commit:
            raise RuntimeError(f'Missing exact source revision in {path.name}')
        if 'README.md' not in names or 'THIRD-PARTY-NOTICES.md' not in names:
            raise RuntimeError(f'Missing package documentation in {path.name}')
        dependencies = []
        for element in metadata.iter():
            if local_name(element) == 'dependency':
                dependency = dict(element.attrib)
                dependencies.append(dependency)
                if dependency['id'].startswith(('XamlX', 'XamlG.XamlX', 'XamlG.Upstream')):
                    raise RuntimeError('Test-only upstream code must not be a package dependency.')
                if dependency['id'].startswith('XamlG.') and version not in dependency.get('version', ''):
                    raise RuntimeError('Internal package versions are not aligned.')
        if any('XamlG.XamlX' in name or 'XamlG.Upstream' in name for name in names):
            raise RuntimeError('Test-only upstream assembly leaked into production package.')
        if package['id'] == 'XamlG.Generator':
            for component in ('Generator', 'Syntax', 'Roslyn', 'Compiler', 'CSharp', 'Frameworks'):
                if f'analyzers/dotnet/cs/XamlG.{component}.dll' not in names:
                    raise RuntimeError(f'Missing analyzer dependency {component}')
            if any(name.startswith('analyzers/') and '/Microsoft.CodeAnalysis' in name for name in names):
                raise RuntimeError('The analyzer must use the compiler host Roslyn assemblies.')
            for suffix in ('props', 'targets'):
                if f'buildTransitive/XamlG.Generator.{suffix}' not in names:
                    raise RuntimeError('Missing transitive MSBuild integration.')
        if 'command' in package:
            configs = [name for name in names if name.endswith('/DotnetToolSettings.xml')]
            if len(configs) != 1 or package['command'] not in archive.read(configs[0]).decode():
                raise RuntimeError('The .NET tool entry point is missing.')
        return {'id': package['id'], 'version': version, 'file': path.name,
                'sha256': hashlib.sha256(path.read_bytes()).hexdigest(), 'dependencies': dependencies}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--version', required=True)
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/release')
    parser.add_argument('--no-build', action='store_true')
    args = parser.parse_args()
    if not VERSION.fullmatch(args.version):
        parser.error('Use a three-part NuGet version, optionally followed by a prerelease label.')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    if any(output.iterdir()):
        raise RuntimeError('Release output must be an empty directory; existing artifacts will not be overwritten.')
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    manifest = json.loads((ROOT / 'eng/release-manifest.json').read_text())['packages']
    if len({item['id'] for item in manifest}) != len(manifest):
        raise RuntimeError('Duplicate shipping package identity.')
    properties = [f'-p:Version={args.version}', f'-p:PackageVersion={args.version}',
                  f'-p:RepositoryCommit={commit}', '-p:ContinuousIntegrationBuild=true', '-p:NuGetAudit=false']
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
        inventory.append(inspect_package(path, package, args.version, commit))
    if len(list(output.glob('*.nupkg'))) != len(manifest):
        raise RuntimeError('The output package set differs from the release manifest.')
    source = output / f'XamlG.{args.version}.source.tar.gz'
    run('git', 'archive', '--format=tar.gz', '-o', str(source), commit)
    (output / 'package-inventory.json').write_text(json.dumps({'sourceCommit': commit, 'version': args.version, 'packages': inventory}, indent=2) + '\n')
    (output / 'release-notes.md').write_text(
        f'# XamlG {args.version}\n\nSource commit: `{commit}`.\n\n'
        'This development release contains the modular compiler, framework adapter, runtime, generator, tooling, workspace, language-server, automation, MCP and coding-agent libraries, official OpenAI/Anthropic/Gemini SDK adapters, and the xamlg/xamlg-lsp/xamlg-studio tools.\n\n'
        'See the included README and documentation for validated behavior and compatibility boundaries. '
        'The upstream baseline and XamlG compatibility suites are separate. Browser isolation protects editor origin access, not operating-system resource quotas.\n\n'
        'SHA256SUMS covers every attached package, symbol package, source archive, inventory and this file. '
        'The inventory records declared NuGet dependencies; it is not a vulnerability scan or a complete SBOM.\n')
    checksums = [f'{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}' for path in sorted(output.iterdir()) if path.is_file()]
    (output / 'SHA256SUMS').write_text('\n'.join(checksums) + '\n')
    print(f'Verified {len(inventory)} packages from {commit}; artifacts: {output}')


if __name__ == '__main__':
    main()
