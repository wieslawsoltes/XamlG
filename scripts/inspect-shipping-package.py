#!/usr/bin/env python3
"""Require the single-reference package to actually export build/analyzer dependencies."""
import argparse
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--packages', type=Path, required=True)
    parser.add_argument('--version', required=True)
    args = parser.parse_args()
    bundle = args.packages / f'XamlG.Avalonia.{args.version}.nupkg'
    with zipfile.ZipFile(bundle) as archive:
        name = next(name for name in archive.namelist() if name.endswith('.nuspec'))
        xml = ET.fromstring(archive.read(name))
        dependencies = {e.get('id'): e for e in xml.iter() if e.tag.rsplit('}', 1)[-1] == 'dependency'}
        for required in ('XamlG.Generator', 'XamlG.AvaloniaRuntime'):
            if required not in dependencies:
                raise RuntimeError('Single-reference package omitted required dependency: ' + required + '\n' + ET.tostring(xml, encoding='unicode'))
        generator = dependencies['XamlG.Generator']
        excluded = {s.strip().lower() for s in generator.get('exclude', '').split(',')}
        if excluded.intersection({'all', 'build', 'buildtransitive', 'analyzers'}):
            raise RuntimeError('The generator dependency incorrectly hides build/analyzer assets: ' + str(generator.attrib))
        print('Verified compiler bundle dependencies: ' + str({k: dict(v.attrib) for k, v in dependencies.items()}))
    generator_package = args.packages / f'XamlG.Generator.{args.version}.nupkg'
    with zipfile.ZipFile(generator_package) as archive:
        for required in ('buildTransitive/NormalizeXamlGInputs.cs', 'lib/netstandard2.0/_._'):
            if required not in archive.namelist():
                raise RuntimeError('Generator package missing integration asset: ' + required)
    print('PASS: transitive compiler/runtime contracts and generator task payload.')


if __name__ == '__main__':
    main()
