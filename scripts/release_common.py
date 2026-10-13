"""Shared package inventory, identity and artifact checks for release commands."""
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = re.compile(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?')
REPOSITORY = 'https://github.com/wieslawsoltes/XamlG'


def validate_version(version):
    if not VERSION.fullmatch(version):
        raise ValueError('Use a three-part NuGet version, optionally followed by a prerelease label.')
    if '-' in version:
        for part in version.split('-', 1)[1].split('.'):
            if part.isdigit() and len(part) > 1 and part.startswith('0'):
                raise ValueError('Numeric prerelease identifiers cannot have leading zeroes.')
    return version


def load_manifest(root=ROOT):
    packages = json.loads((root / 'eng/release-manifest.json').read_text())['packages']
    ids = [item['id'].casefold() for item in packages]
    paths = [item['project'] for item in packages]
    if not packages or len(set(ids)) != len(ids) or len(set(paths)) != len(paths):
        raise RuntimeError('Empty or duplicate shipping package identity/project.')
    # Production projects declare IsPackable in their project file; samples/tests
    # are deliberately outside this inventory. Any new production project must
    # ship or explicitly opt out, even when no other package references it yet.
    production = {}
    for directory in ('src', 'tools'):
        for project in sorted((root / directory).glob('*/*.csproj')):
            xml = ET.parse(project)
            if xml.findtext('.//IsPackable', 'true').lower() != 'false':
                production[project.relative_to(root).as_posix()] = xml
    if set(paths) != set(production):
        raise RuntimeError(f'Shipping project inventory mismatch: unlisted={sorted(set(production) - set(paths))}, invalid={sorted(set(paths) - set(production))}')
    solution = {item.attrib['Path'] for item in ET.parse(root / 'XamlG.slnx').iter('Project')}
    for package in packages:
        project = root / package['project']
        if not project.resolve().is_relative_to(root.resolve()) or package['project'] not in solution:
            raise RuntimeError(f'Shipping project must be inside the repository and shipping solution: {project}')
        xml = production[package['project']]
        if xml.findtext('.//PackageId', project.stem) != package['id']:
            raise RuntimeError(f'Package identity differs from the project: {project}')
        command = xml.findtext('.//ToolCommandName') if xml.findtext('.//PackAsTool', 'false').lower() == 'true' else None
        if package.get('command') != command:
            raise RuntimeError(f'Tool command differs from the project: {project}')
        if not xml.findtext('.//Description', '').strip():
            raise RuntimeError(f'Shipping package needs a specific description: {project}')
    return packages


def local_name(element):
    return element.tag.rsplit('}', 1)[-1]


def inspect_package(path, package, version, commit, shipping_ids):
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
        if repository is None or repository.get('commit') != commit or repository.get('url') != REPOSITORY:
            raise RuntimeError(f'Missing exact source revision in {path.name}')
        if not {'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md'}.issubset(names) or values.get('readme') is None or values['readme'].text != 'README.md':
            raise RuntimeError(f'Missing package documentation in {path.name}')
        if values.get('projectUrl') is None or values['projectUrl'].text != REPOSITORY:
            raise RuntimeError(f'Missing package project URL in {path.name}')
        if not values.get('description', ET.Element('description')).text:
            raise RuntimeError(f'Missing package description in {path.name}')
        dependencies = []
        for element in metadata.iter():
            if local_name(element) == 'dependency':
                dependency = dict(element.attrib)
                dependencies.append(dependency)
                if dependency['id'].startswith(('XamlX', 'XamlG.XamlX', 'XamlG.Upstream')):
                    raise RuntimeError('Test-only upstream code must not be a package dependency.')
                if dependency['id'].casefold().startswith('xamlg.'):
                    if dependency['id'] not in shipping_ids:
                        raise RuntimeError('Internal dependency is missing from the release manifest: ' + dependency['id'])
                    if dependency.get('version') not in {version, f'[{version}]', f'[{version}, )'}:
                        raise RuntimeError('Internal package versions are not aligned: ' + dependency['id'])
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


def verify_candidate(root, manifest, version, commit, *, allow_dirty=False):
    validate_version(version)
    inventory = json.loads((root / 'package-inventory.json').read_text())
    if inventory.get('version') != version or inventory.get('sourceCommit') != commit or not re.fullmatch('[0-9a-f]{40}', commit):
        raise RuntimeError('Candidate version/source identity does not match the release tag.')
    if inventory.get('sourceDirty') is not False and not (allow_dirty and inventory.get('sourceDirty') is True):
        raise RuntimeError('Only a candidate built from a clean, recorded source revision can be published.')
    expected = {'SHA256SUMS'}
    for line in (root / 'SHA256SUMS').read_text().splitlines():
        digest, name = line.split('  ', 1)
        if Path(name).name != name or name in {'.', '..'} or '\\' in name or name in expected:
            raise RuntimeError('Invalid or duplicated checksummed filename.')
        path = root / name
        if path.is_symlink() or not path.is_file() or not re.fullmatch('[0-9a-f]{64}', digest):
            raise RuntimeError('Invalid checksummed file: ' + name)
        if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            raise RuntimeError('Release checksum mismatch: ' + name)
        expected.add(name)
    if {path.name for path in root.iterdir()} != expected:
        raise RuntimeError('Candidate contains files not covered by SHA256SUMS.')
    required = {'SHA256SUMS', 'package-inventory.json', 'release-notes.md', f'XamlG.{version}.source.tar.gz'}
    shipping_ids = {package['id'] for package in manifest}
    inspected = []
    for package in manifest:
        filename = f"{package['id']}.{version}.nupkg"
        required.add(filename)
        inspected.append(inspect_package(root / filename, package, version, commit, shipping_ids))
        if package['id'] != 'XamlG.Generator' and 'command' not in package:
            required.add(f"{package['id']}.{version}.snupkg")
    if expected != required:
        raise RuntimeError('Release artifacts differ from the shipping manifest, including symbols and source.')
    if inventory.get('packages') != inspected:
        raise RuntimeError('Package inventory differs from the inspected package contents.')
    return inventory

