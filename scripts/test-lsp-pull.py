#!/usr/bin/env python3
"""Exercise negotiated pull reports, coherent resource dependencies and refresh replies in the real stdio host."""
import importlib.util
import json
from pathlib import Path
import sys
import tempfile

spec = importlib.util.spec_from_file_location('lsp_client', Path(__file__).with_name('test-lsp-host.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def reply(client, request, error=False):
    message = {'jsonrpc': '2.0', 'id': request['id']}
    message['error' if error else 'result'] = {'code': -32601, 'message': 'not implemented'} if error else None
    payload = json.dumps(message).encode('utf8')
    client.process.stdin.write(f'Content-Length: {len(payload)}\r\n\r\n'.encode('ascii') + payload)
    client.process.stdin.flush()


def main():
    host = Path(sys.argv[1]).resolve()
    project = Path('tests/ResourceWorkspaceSmoke/ResourceWorkspaceSmoke.csproj').resolve()
    caller_path = project.with_name('Main.axaml')
    resource_path = project.with_name('Values.axaml')
    caller, resource = caller_path.as_uri(), resource_path.as_uri()
    with tempfile.TemporaryFile(mode='w+b') as stderr:
        client = module.Client(['dotnet', str(host), '--project', str(project), '--trust-project', '--framework', 'Avalonia', '--no-watch'], stderr)
        try:
            options = {'capabilities': {'textDocument': {'diagnostic': {'relatedDocumentSupport': True}},
                                       'workspace': {'diagnostics': {'refreshSupport': True}}}}
            capabilities = client.request('initialize', options)['capabilities']
            assert capabilities['diagnosticProvider'] == {'identifier': 'xamlg', 'interFileDependencies': True, 'workspaceDiagnostics': True}
            client.send('initialized', {})

            def workspace(previous=()):
                return client.request('workspace/diagnostic', {'identifier': 'xamlg', 'previousResultIds': list(previous)})['items']

            def previous(reports):
                return [{'uri': r['uri'], 'value': r['resultId']} for r in reports if 'resultId' in r]

            def pull(uri, token=None):
                parameters = {'textDocument': {'uri': uri}, 'identifier': 'xamlg'}
                if token is not None:
                    parameters['previousResultId'] = token
                return client.request('textDocument/diagnostic', parameters)

            initial = workspace()
            assert {r['uri'] for r in initial} == {caller, resource}
            assert all(r['kind'] == 'full' and r['items'] == [] and r['version'] is None for r in initial)
            initial_ids = {r['uri']: r['resultId'] for r in initial}
            assert initial_ids[caller] != initial_ids[resource], 'Cache tokens are document-scoped.'
            same = workspace(previous(initial))
            assert all(r['kind'] == 'unchanged' and 'items' not in r for r in same)

            client.send('textDocument/didOpen', {'textDocument': {'uri': caller, 'languageId': 'xaml', 'version': 1, 'text': caller_path.read_text()}})
            unchanged = pull(caller, initial_ids[caller])
            assert unchanged['kind'] == 'unchanged' and 'items' not in unchanged
            assert resource in unchanged['relatedDocuments']
            refresh = client.receive(lambda m: m.get('method') == 'workspace/diagnostic/refresh')
            reply(client, refresh)
            # Unknown server-response IDs are ignored, not answered with another JSON-RPC response.
            reply(client, {'id': 'not-owned-by-this-server'})

            broken = resource_path.read_text().replace('>7<', '>invalid<')
            assert broken != resource_path.read_text()
            client.send('textDocument/didOpen', {'textDocument': {'uri': resource, 'languageId': 'xaml', 'version': 4, 'text': broken}})
            failed = pull(caller, initial_ids[caller])
            assert failed['kind'] == 'full' and any(d['code'] == 'XG3305' for d in failed['items'])
            assert failed['relatedDocuments'][resource]['items'], 'Include failures must carry related resource diagnostics.'
            reported = workspace(previous(initial))
            indexed = {r['uri']: r for r in reported}
            assert indexed[caller]['version'] == 1 and indexed[resource]['version'] == 4
            assert all(r['kind'] == 'full' for r in reported)
            repeated = workspace(previous(reported))
            assert all(r['kind'] == 'unchanged' for r in repeated)
            assert pull(caller, indexed[resource]['resultId'])['kind'] == 'full', 'A foreign token must not produce unchanged.'
            assert pull(caller, 'expired-result-id')['kind'] == 'full'

            # Document versions can change while the actual diagnostic values remain identical.
            client.send('textDocument/didChange', {'textDocument': {'uri': caller, 'version': 2},
                                                   'contentChanges': [{'text': caller_path.read_text() + '\n'}]})
            assert pull(caller, indexed[caller]['resultId'])['kind'] == 'unchanged'
            client.send('textDocument/didClose', {'textDocument': {'uri': resource}})
            recovered = pull(caller, indexed[caller]['resultId'])
            assert recovered['kind'] == 'full' and recovered['items'] == []
            after_close = {r['uri']: r for r in workspace()}
            assert after_close[caller]['version'] == 2 and after_close[resource]['version'] is None

            scratch = 'untitled:Scratch.axaml'
            client.send('textDocument/didOpen', {'textDocument': {'uri': scratch, 'languageId': 'xaml', 'version': 8, 'text': '<Broken'}})
            scratch_result = pull(scratch)
            assert scratch_result['items']
            all_open = workspace()
            client.send('textDocument/didClose', {'textDocument': {'uri': scratch}})
            clears = {r['uri']: r for r in workspace(previous(all_open))}
            assert clears[scratch]['kind'] == 'full' and clears[scratch]['items'] == [] and clears[scratch]['version'] is None

            # Reply to an outstanding refresh and verify the protocol still serves ordinary requests.
            refresh = client.receive(lambda m: m.get('method') == 'workspace/diagnostic/refresh')
            reply(client, refresh, error=True)
            assert 'generated' in client.request('xamlg/inspect', {'textDocument': {'uri': caller}})
            assert not any(m.get('method') == 'textDocument/publishDiagnostics' for m in client.pending), 'Pull mode must not duplicate push diagnostics.'
            assert not any('error' in m for m in client.pending), 'The server must not answer a client response.'
            client.request('shutdown')
            client.send('exit'); client.process.stdin.close()
            assert client.process.wait(timeout=15) == 0
            print('PASS: pull capability, closed/workspace reports, stable/foreign IDs, related dependencies, unchanged versions, removal clears, refresh replies and graceful shutdown.')
        except Exception:
            stderr.seek(0)
            print(stderr.read().decode('utf8', errors='replace'), file=sys.stderr)
            raise
        finally:
            if client.process.poll() is None:
                client.process.kill(); client.process.wait(timeout=5)


if __name__ == '__main__':
    main()
