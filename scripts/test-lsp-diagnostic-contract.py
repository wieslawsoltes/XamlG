#!/usr/bin/env python3
"""Exercise opaque diagnostic IDs, invalid-input recovery, unsaved C# dependencies and push/pull parity."""
import importlib.util
from pathlib import Path
import sys
import tempfile

spec = importlib.util.spec_from_file_location('lsp_client', Path(__file__).with_name('test-lsp-host.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def run_mode(host, negotiated):
    with tempfile.TemporaryDirectory(prefix='xamlg-pull-contract-') as folder, tempfile.TemporaryFile(mode='w+b') as stderr:
        root = Path(folder)
        code = root / 'Model.cs'
        original = 'namespace DiagnosticContracts { public class View { public string Text {get;set;} } }'
        code.write_text(original, encoding='utf8')
        code_uri = code.as_uri()
        uri = (root / 'View.axaml').as_uri()
        source = "<View xmlns='clr-namespace:DiagnosticContracts' Text='hello'/>"
        client = module.Client(['dotnet', str(host), '--code', str(code), '--framework', 'Portable', '--no-watch'], stderr)
        try:
            capabilities = {'textDocument': {'diagnostic': {'relatedDocumentSupport': True}}} if negotiated else {}
            client.request('initialize', {'capabilities': capabilities})
            client.send('initialized', {})

            def pull(previous=None):
                params = {'textDocument': {'uri': uri}, 'identifier': 'xamlg'}
                if previous is not None:
                    params['previousResultId'] = previous
                return client.request('textDocument/diagnostic', params)

            def reject(method, params):
                client.sequence += 1
                sequence = client.sequence
                client.send(method, params, request_id=sequence)
                response = client.receive(lambda message: message.get('id') == sequence)
                assert response.get('error', {}).get('code') == -32602, response

            client.send('textDocument/didOpen', {'textDocument': {'uri': uri, 'languageId': 'xaml', 'version': 1, 'text': source}})
            if not negotiated:
                assert client.diagnostics(uri, 1) == []
            first = pull('')
            assert first['kind'] == 'full' and first['items'] == [] and first['resultId']
            unchanged = pull(first['resultId'])
            assert unchanged['kind'] == 'unchanged' and 'items' not in unchanged
            reports = client.request('workspace/diagnostic', {'previousResultIds': [{'uri': uri, 'value': ''}]})['items']
            report = next(item for item in reports if item['uri'] == uri)
            assert report['kind'] == 'full' and report['items'] == [] and report['version'] == 1

            # Invalid protocol inputs must not poison the cache or terminate the server.
            for token in (None, 17, {}, 'x' * 257):
                reject('textDocument/diagnostic', {'textDocument': {'uri': uri}, 'previousResultId': token})
            for previous in ([{'uri': '', 'value': ''}], [{'uri': uri, 'value': None}],
                             [{'uri': uri, 'value': ''}, {'uri': uri, 'value': ''}]):
                reject('workspace/diagnostic', {'previousResultIds': previous})
            for identifier in ('unknown-provider', '', 7):
                reject('textDocument/diagnostic', {'textDocument': {'uri': uri}, 'identifier': identifier})
            assert pull(first['resultId'])['kind'] == 'unchanged'

            # The XAML client version stays at 1 while an unsaved C# buffer changes its meaning.
            client.send('textDocument/didOpen', {'textDocument': {'uri': code_uri, 'languageId': 'csharp', 'version': 7,
                                                               'text': original.replace(' Text ', ' Caption ')}})
            broken = pull(first['resultId'])
            assert broken['kind'] == 'full' and any(item['code'] == 'XG1005' for item in broken['items'])
            if not negotiated:
                pushed = client.receive(lambda message: message.get('method') == 'textDocument/publishDiagnostics'
                    and message['params']['uri'] == uri and message['params'].get('version') == 1
                    and any(item['code'] == 'XG1005' for item in message['params']['diagnostics']))
                assert pushed['params']['diagnostics'] == broken['items']
            report = next(item for item in client.request('workspace/diagnostic', {'previousResultIds': []})['items'] if item['uri'] == uri)
            assert report['version'] == 1 and report['items'] == broken['items']
            assert pull(broken['resultId'])['kind'] == 'unchanged'
            client.send('textDocument/didChange', {'textDocument': {'uri': code_uri, 'version': 8},
                                                 'contentChanges': [{'text': original + ' // unsaved recovery'}]})
            recovered = pull(broken['resultId'])
            assert recovered['kind'] == 'full' and recovered['items'] == []
            client.send('textDocument/didClose', {'textDocument': {'uri': code_uri}})
            assert pull(recovered['resultId'])['kind'] == 'unchanged'
            assert code.read_text(encoding='utf8') == original, 'C# synchronization must not write the file.'

            client.send('textDocument/didChange', {'textDocument': {'uri': uri, 'version': 2}, 'contentChanges': [{'text': '<Broken'}]})
            malformed = pull()
            assert malformed['kind'] == 'full' and malformed['items']
            if not negotiated:
                assert client.diagnostics(uri, 2) == malformed['items']
            client.send('textDocument/didChange', {'textDocument': {'uri': uri, 'version': 3},
                                                 'contentChanges': [{'text': source.replace('hello', 'Zażółć · 😀')}]})
            repaired = pull(malformed['resultId'])
            assert repaired['kind'] == 'full' and repaired['items'] == []
            client.send('textDocument/didClose', {'textDocument': {'uri': uri}})
            cleared = client.request('workspace/diagnostic', {'previousResultIds': [{'uri': uri, 'value': malformed['resultId']}]})['items']
            report = next(item for item in cleared if item['uri'] == uri)
            assert report['kind'] == 'full' and report['items'] == [] and report['version'] is None
            if negotiated:
                assert not any(message.get('method') == 'textDocument/publishDiagnostics' for message in client.pending)
            client.request('shutdown')
            client.send('exit')
            client.process.stdin.close()
            assert client.process.wait(timeout=15) == 0
            print('PASS: diagnostic contract, empty opaque IDs, rejected inputs, unsaved C# dependency, Unicode/recovery, and '
                  + ('negotiated pull' if negotiated else 'legacy push/pull parity') + '.')
        except Exception:
            stderr.seek(0)
            print(stderr.read().decode('utf8', errors='replace'), file=sys.stderr)
            raise
        finally:
            if client.process.poll() is None:
                client.process.kill()
                client.process.wait(timeout=5)


if __name__ == '__main__':
    host = Path(sys.argv[1]).resolve()
    run_mode(host, True)
    run_mode(host, False)
