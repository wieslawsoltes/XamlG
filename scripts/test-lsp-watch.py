#!/usr/bin/env python3
"""Verify that the real LSP process refreshes C# compiler inputs without changing XAML versions."""
import importlib.util
from pathlib import Path
import sys
import tempfile

spec = importlib.util.spec_from_file_location('lsp_client', Path(__file__).with_name('test-lsp-host.py'))
client_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(client_module)


def main():
    host = Path(sys.argv[1]).resolve()
    with tempfile.TemporaryDirectory() as folder, tempfile.TemporaryFile(mode='w+b') as stderr:
        model = Path(folder) / 'Model.cs'
        model.write_text('namespace Watch { public class View { public string Text {get;set;} } }')
        client = client_module.Client(['dotnet', str(host), '--code', str(model), '--framework', 'Portable'], stderr)
        try:
            client.request('initialize', {'capabilities': {}})
            client.send('initialized', {})
            uri = (Path(folder) / 'View.xaml').as_uri()
            client.send('textDocument/didOpen', {'textDocument': {'uri': uri, 'languageId': 'xaml', 'version': 1, 'text': "<View xmlns='clr-namespace:Watch' Text='hello'/>"}})
            assert client.diagnostics(uri, 1) == []
            model.write_text('namespace Watch { public class View { public int Count {get;set;} } }')
            changed = client.receive(lambda m: m.get('method') == 'textDocument/publishDiagnostics' and m['params']['uri'] == uri and any(d['code'] == 'XG1005' for d in m['params']['diagnostics']))
            assert changed['params']['version'] == 1
            temporary = model.with_suffix('.tmp')
            temporary.write_text('namespace Watch { public class View { public string Text {get;set;} } }')
            temporary.replace(model)
            client.receive(lambda m: m.get('method') == 'textDocument/publishDiagnostics' and m['params']['uri'] == uri and m['params']['diagnostics'] == [])
            client.request('shutdown')
            client.send('exit')
            client.process.stdin.close()
            assert client.process.wait(timeout=15) == 0
            print('PASS: compiler-input change, unchanged XAML version, refreshed diagnostics, atomic-save recovery, clean shutdown.')
        except Exception:
            stderr.seek(0)
            print(stderr.read().decode('utf-8', errors='replace'), file=sys.stderr)
            raise
        finally:
            if client.process.poll() is None:
                client.process.kill()
                client.process.wait(timeout=5)


if __name__ == '__main__':
    main()
