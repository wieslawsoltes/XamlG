#!/usr/bin/env python3
"""Verify pre-rename text edits for files/folders without allowing the server to change any file."""
import importlib.util
from pathlib import Path
import sys
import tempfile

spec = importlib.util.spec_from_file_location('lsp_client', Path(__file__).with_name('test-lsp-host.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def exercise(host, versioned):
    project = Path('tests/FileRenameSmoke/FileRenameSmoke.csproj').resolve()
    root = project.parent
    main, palette, base = root / 'Main.axaml', root / 'Theme/Palette.axaml', root / 'Theme/Base.axaml'
    before = {path: path.read_bytes() for path in (main, palette, base, project)}
    with tempfile.TemporaryFile(mode='w+b') as stderr:
        client = module.Client(['dotnet', str(host), '--project', str(project), '--trust-project', '--framework', 'Avalonia', '--no-watch'], stderr)
        try:
            capabilities = client.request('initialize', {'capabilities': {'workspace': {
                'workspaceEdit': {'documentChanges': versioned}, 'fileOperations': {'willRename': True}}}})['capabilities']
            assert capabilities['workspace']['fileOperations']['willRename']['filters']
            assert 'didRename' not in capabilities['workspace']['fileOperations'], 'Only implemented notifications are advertised.'
            client.send('initialized', {})
            client.send('textDocument/didOpen', {'textDocument': {'uri': main.as_uri(), 'languageId': 'xaml', 'version': 7, 'text': main.read_text()}})
            assert client.diagnostics(main.as_uri(), 7) == []

            def moves(*pairs):
                return {'files': [{'oldUri': str(old) if isinstance(old, str) else old.as_uri(),
                                   'newUri': str(new) if isinstance(new, str) else new.as_uri()} for old, new in pairs]}

            def change_map(response):
                if versioned:
                    assert 'changes' not in response
                    assert all('kind' not in item for item in response['documentChanges']), 'Client-owned file operations must not be returned a second time.'
                    return {item['textDocument']['uri']: item for item in response['documentChanges']}
                assert 'documentChanges' not in response
                return {uri: {'edits': edits} for uri, edits in response['changes'].items()}

            response = change_map(client.request('workspace/willRenameFiles', moves((palette, root / 'Resources/Colors.axaml'))))
            assert set(response) == {main.as_uri(), palette.as_uri()}
            assert response[main.as_uri()]['edits'][0]['newText'] == 'Resources/Colors.axaml'
            assert response[palette.as_uri()]['edits'][0]['newText'] == '../Theme/Base.axaml'
            if versioned:
                assert response[main.as_uri()]['textDocument']['version'] == 7
                assert response[palette.as_uri()]['textDocument']['version'] is None

            folder = change_map(client.request('workspace/willRenameFiles', moves((root / 'Theme', root / 'Styles'))))
            assert set(folder) == {main.as_uri()}
            assert folder[main.as_uri()]['edits'][0]['newText'] == 'Styles/Palette.axaml'
            combined = change_map(client.request('workspace/willRenameFiles', moves((main, root / 'Views/Main.axaml'), (root / 'Theme', root / 'Styles'))))
            assert combined[main.as_uri()]['edits'][0]['newText'] == '../Styles/Palette.axaml'
            assert client.request('workspace/willRenameFiles', moves((root / 'Unrelated.cs', root / 'Other.cs'))) is None

            def rejected(parameters):
                client.sequence += 1
                client.send('workspace/willRenameFiles', parameters, client.sequence)
                value = client.receive(lambda m: m.get('id') == client.sequence)
                assert value['error']['code'] == -32602, value

            rejected(moves((palette, base)))
            rejected(moves((root / 'Theme', root / 'Styles'), (palette, root / 'Colors.axaml')))
            rejected(moves((palette, 'https://example.invalid/Colors.axaml')))
            assert all(path.read_bytes() == content for path, content in before.items()), 'The language server must return edits, not write files.'
            client.request('shutdown'); client.send('exit'); client.process.stdin.close()
            assert client.process.wait(timeout=15) == 0
        except Exception:
            stderr.seek(0)
            print(stderr.read().decode('utf8', errors='replace'), file=sys.stderr)
            raise
        finally:
            if client.process.poll() is None:
                client.process.kill(); client.process.wait(timeout=5)


def main():
    host = Path(sys.argv[1]).resolve()
    exercise(host, True)
    exercise(host, False)
    print('PASS: file/folder moves, incoming/outgoing references, original URI/version ordering, legacy edits, collision/malformed rejection and zero file writes.')


if __name__ == '__main__':
    main()
