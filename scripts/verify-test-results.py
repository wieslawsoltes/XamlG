#!/usr/bin/env python3
"""Verify that a TRX contains the expected executed tests, with no skipped or failed cases."""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('path', type=Path)
    parser.add_argument('--expected', type=int, required=True)
    args = parser.parse_args()
    root = ET.parse(args.path).getroot()
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    results = root.findall('.//t:UnitTestResult', ns)
    counters = root.find('.//t:Counters', ns)
    if counters is None:
        raise SystemExit('Missing test counters: discovery or test-host execution failed.')
    print(json.dumps(dict(counters.attrib), indent=2))
    for result in results:
        if result.get('outcome') != 'Passed':
            message = result.findtext('.//t:Message', default='', namespaces=ns)
            print(f"{result.get('outcome')}: {result.get('testName')}\n{message[:12000]}")
    if len(results) != args.expected:
        raise SystemExit(f'Expected {args.expected} results, received {len(results)}.')
    if any(result.get('outcome') != 'Passed' for result in results):
        raise SystemExit('Every expected case must execute and pass; skips are not successes.')
    if int(counters.get('executed', '0')) != args.expected:
        raise SystemExit('Executed count does not match the expected case count.')
    print(f'Verified {args.expected} executed, passing tests.')


if __name__ == '__main__':
    main()
