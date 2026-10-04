#!/usr/bin/env python3
"""Test coherent unsaved resource overlays using the real trusted-project stdio host."""
import importlib.util
from pathlib import Path
import sys
import tempfile

spec = importlib.util.spec_from_file_location('lsp_client', Path(__file__).with_name('test-lsp-host.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def main():
    host = Path(sys.argv[1]).resolve()
    project = Path('tests/ResourceWorkspaceSmoke/ResourceWorkspaceSmoke.csproj').resolve()
    main_file = project.with_name('Main.axaml')
    resource_file = project.with_name('Values.axaml')
    caller = main_file.as_uri()
    dependency = resource_file.as_uri()
    with tempfile.TemporaryFile(mode='w+b') as stderr:
        client = module.Client(['dotnet', str(host), '--project', str(project), '--trust-project', '--framework', 'Avalonia', '--no-watch'], stderr)
        try:
            client.request('initialize', {'capabilities': {}})
            client.send('initialized', {})
            client.send('textDocument/didOpen', {'textDocument': {'uri': caller, 'languageId': 'xaml', 'version': 1, 'text': main_file.read_text()}})
            assert client.diagnostics(caller, 1) == []
            client.send('textDocument/didOpen', {'textDocument': {'uri': dependency, 'languageId': 'xaml', 'version': 1, 'text': resource_file.read_text()}})
            assert client.diagnostics(dependency, 1) == []
            broken = resource_file.read_text().replace('>7<', '>invalid<')
            client.send('textDocument/didChange', {'textDocument': {'uri': dependency, 'version': 2}, 'contentChanges': [{'text': broken}]})
            failed = client.receive(lambda m: m.get('method') == 'textDocument/publishDiagnostics' and m['params']['uri'] == caller and any(d['code'] == 'XG3305' for d in m['params']['diagnostics']))
            assert failed['params']['version'] == 1, 'The caller version must not be fabricated.'
            inspection = client.request('xamlg/inspect', {'textDocument': {'uri': caller}})
            assert inspection['generated'] == '', 'Semantic requests must use the same unsaved dependency graph.'
            # Closing the unsaved broken resource returns to the immutable loaded on-disk snapshot.
            client.send('textDocument/didClose', {'textDocument': {'uri': dependency}})
            client.receive(lambda m: m.get('method') == 'textDocument/publishDiagnostics' and m['params']['uri'] == caller and m['params']['diagnostics'] == [])
            inspection = client.request('xamlg/inspect', {'textDocument': {'uri': caller}})
            assert 'XamlResourceServices.Enter' in inspection['generated']
            client.request('shutdown')
            client.send('exit')
            client.process.stdin.close()
            assert client.process.wait(timeout=15) == 0
            print('PASS: project includes, coherent unsaved overlays, caller diagnostics at unchanged version, semantic consistency, close/recovery and shutdown.')
        except Exception:
            stderr.seek(0)
            print(stderr.read().decode('utf8', errors='replace'), file=sys.stderr)
            raise
        finally:
            if client.process.poll() is None:
                client.process.kill()
                client.process.wait(timeout=5)


if __name__ == '__main__':
    main()
