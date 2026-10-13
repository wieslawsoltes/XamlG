#!/usr/bin/env python3
"""Resolve a candidate version and reject publication from branches or unmerged tags."""
import os
import subprocess
import xml.etree.ElementTree as ET

from release_common import ROOT, validate_version


def resolve_version(ref, ref_name, run_number, publish, base_version):
    tagged = ref.startswith('refs/tags/v')
    if publish and not tagged:
        raise ValueError('NuGet publishing requires an existing v<version> tag, not a branch.')
    if ref.startswith('refs/tags/') and not tagged:
        raise ValueError('Release tags must start with v.')
    version = ref_name[1:] if tagged else base_version.split('-', 1)[0] + '-ci.' + run_number
    return validate_version(version)


def main():
    version = resolve_version(os.environ['REF'], os.environ['REF_NAME'], os.environ['RUN_NUMBER'],
                              os.environ.get('PUBLISH_NUGET') == 'true',
                              ET.parse(ROOT / 'Directory.Build.props').findtext('.//Version'))
    if os.environ['REF'].startswith('refs/tags/'):
        subprocess.run(['git', 'merge-base', '--is-ancestor', 'HEAD', 'origin/main'], cwd=ROOT, check=True)
    with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
        output.write('version=' + version + '\n')
    print('Release version:', version)


if __name__ == '__main__':
    main()
