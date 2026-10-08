#!/usr/bin/env python3
"""Prepare the existing Playground publish for its GitHub Pages repository path."""
import argparse
import json
from pathlib import Path
import re


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', default='artifacts/playground/wwwroot')
    parser.add_argument('--commit', required=True)
    args = parser.parse_args()
    if not re.fullmatch(r'[0-9a-f]{40}', args.commit):
        parser.error('--commit must be the full source commit.')
    site = Path(args.directory)
    index = site / 'index.html'
    source = index.read_text()
    if source.count('<base href="/" />') != 1:
        parser.error('Expected one explicit root application base URI in a fresh publish.')
    source = source.replace('<base href="/" />', '<base href="/XamlG/" />')
    index.write_text(source)
    (site / '404.html').write_text(source)
    (site / '.nojekyll').write_text('')
    (site / 'build.json').write_text(json.dumps({'commit': args.commit, 'application': 'XamlG Compiler Studio'}))


if __name__ == '__main__':
    main()
