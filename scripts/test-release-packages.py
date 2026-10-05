#!/usr/bin/env python3
"""Install packaged tools and build real consumers against the candidate XamlG feed."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def run(*args, env=None):
    subprocess.run(args, cwd=ROOT, env=env, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--packages', type=Path, required=True)
    parser.add_argument('--version', required=True)
    args = parser.parse_args()
    packages = args.packages.resolve()
    with tempfile.TemporaryDirectory(prefix='xamlg-release-') as temporary:
        work = Path(temporary)
        configuration = ET.Element('configuration')
        sources = ET.SubElement(configuration, 'packageSources')
        ET.SubElement(sources, 'clear')
        ET.SubElement(sources, 'add', key='candidate', value=str(packages))
        ET.SubElement(sources, 'add', key='nuget', value='https://api.nuget.org/v3/index.json')
        mapping = ET.SubElement(configuration, 'packageSourceMapping')
        candidate = ET.SubElement(mapping, 'packageSource', key='candidate')
        ET.SubElement(candidate, 'package', pattern='XamlG.*')
        public = ET.SubElement(mapping, 'packageSource', key='nuget')
        ET.SubElement(public, 'package', pattern='*')
        config = work / 'NuGet.Config'
        ET.ElementTree(configuration).write(config, encoding='utf-8', xml_declaration=True)
        environment = dict(os.environ, NUGET_PACKAGES=str(work / 'packages'))
        tools = work / 'tools'
        for package, command in (('XamlG.Cli', 'xamlg'), ('XamlG.Lsp', 'xamlg-lsp')):
            run('dotnet', 'tool', 'install', package, '--tool-path', str(tools), '--version', args.version, '--configfile', str(config), env=environment)
            executable = tools / (command + ('.exe' if os.name == 'nt' else ''))
            run(str(executable), '--help', env=environment)
        cli = str(tools / ('xamlg.exe' if os.name == 'nt' else 'xamlg'))
        run(cli, 'compile', '--file', 'tests/CliSmoke/View.xaml', '--code', 'tests/CliSmoke/Model.cs',
            '--framework', 'Portable', '--output', str(work / 'generated'), '--emit-assembly', str(work / 'generated/View.dll'), env=environment)
        if (work / 'generated/View.dll').stat().st_size == 0:
            raise RuntimeError('Installed compiler did not emit an assembly.')
        lsp = list((tools / '.store').glob('**/tools/net10.0/any/XamlG.Lsp.dll'))
        if len(lsp) != 1:
            raise RuntimeError('Installed language-server entry point is missing or ambiguous.')
        run('git', 'ls-files', '-s', '--', 'scripts/test-lsp-*.py', env=environment)
        for script in ('host', 'watch', 'features', 'csharp', 'diagnostic-contract'):
            run('python', f'scripts/test-lsp-{script}.py', str(lsp[0]), env=environment)
        for project in ('tests/ResourceWorkspaceSmoke/ResourceWorkspaceSmoke.csproj', 'tests/FileRenameSmoke/FileRenameSmoke.csproj'):
            run('dotnet', 'restore', project, '--configfile', str(config), '-p:NuGetAudit=false', env=environment)
        for script in ('resources', 'pull', 'file-moves'):
            run('python', f'scripts/test-lsp-{script}.py', str(lsp[0]), env=environment)
        for project in ('tests/PackagingSmoke/PackagingSmoke.csproj', 'tests/AvaloniaPackagingSmoke/AvaloniaPackagingSmoke.csproj'):
            run('dotnet', 'restore', project, '--configfile', str(config), f'-p:XamlGPackageVersion={args.version}', '-p:NuGetAudit=false', env=environment)
            run('dotnet', 'run', '--project', project, '-c', 'Release', '--no-restore', f'-p:XamlGPackageVersion={args.version}', env=environment)
        project = 'tests/WorkspaceSmoke/WorkspaceSmoke.csproj'
        run('dotnet', 'restore', project, '--configfile', str(config), '-p:NuGetAudit=false', env=environment)
        run(cli, 'compile', '--project', project, '--framework', 'Portable', '--output', str(work / 'project'),
            '--emit-assembly', str(work / 'project/WorkspaceSmoke.dll'), env=environment)
        run('python', 'scripts/test-shipping-consumer.py', '--config', str(config), '--version', args.version, '--cli', cli, env=environment)
        print('PASS: installed CLI/LSP, protocol suites, portable/Avalonia consumers, single-reference shipping and evaluated resource emission.')


if __name__ == '__main__':
    main()
