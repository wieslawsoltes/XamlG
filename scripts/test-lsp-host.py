#!/usr/bin/env python3
"""Exercise the actual stdio process, not a mocked language-service API."""
import json
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import time


class Client:
    def __init__(self, command, stderr):
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=stderr)
        self.messages = queue.Queue()
        self.pending = []
        self.sequence = 0
        threading.Thread(target=self.read_loop, daemon=True).start()

    def read_loop(self):
        try:
            while True:
                length = None
                while True:
                    line = self.process.stdout.readline(8193)
                    if not line:
                        raise EOFError('Server closed stdout.')
                    if line == b'\r\n':
                        break
                    if len(line) > 8192:
                        raise ValueError('Response header exceeds limit.')
                    name, value = line.decode('ascii').split(':', 1)
                    if name.lower() == 'content-length':
                        if length is not None:
                            raise ValueError('Duplicate Content-Length.')
                        length = int(value.strip())
                if length is None or not 0 < length <= 8_388_608:
                    raise ValueError('Invalid Content-Length.')
                payload = bytearray()
                while len(payload) < length:
                    chunk = self.process.stdout.read(length - len(payload))
                    if not chunk:
                        raise EOFError('Truncated response.')
                    payload.extend(chunk)
                self.messages.put(json.loads(payload))
        except Exception as error:
            self.messages.put(error)

    def send(self, method, params=None, request_id=None):
        message = {'jsonrpc': '2.0', 'method': method}
        if params is not None:
            message['params'] = params
        if request_id is not None:
            message['id'] = request_id
        payload = json.dumps(message, ensure_ascii=False).encode('utf-8')
        self.process.stdin.write(f'Content-Length: {len(payload)}\r\n\r\n'.encode('ascii') + payload)
        self.process.stdin.flush()

    def receive(self, predicate, timeout=30):
        for index, message in enumerate(self.pending):
            if predicate(message):
                return self.pending.pop(index)
        deadline = time.monotonic() + timeout
        while True:
            message = self.messages.get(timeout=max(0.001, deadline - time.monotonic()))
            if isinstance(message, Exception):
                raise message
            if predicate(message):
                return message
            self.pending.append(message)
            if time.monotonic() >= deadline:
                raise TimeoutError('Expected protocol message did not arrive.')

    def request(self, method, params=None):
        self.sequence += 1
        request_id = self.sequence
        self.send(method, params, request_id)
        response = self.receive(lambda value: value.get('id') == request_id)
        if 'error' in response:
            raise AssertionError(response['error'])
        return response['result']

    def diagnostics(self, uri, version):
        return self.receive(lambda message: message.get('method') == 'textDocument/publishDiagnostics'
                            and message['params']['uri'] == uri
                            and message['params'].get('version') == version)['params']['diagnostics']


def main():
    host = Path(sys.argv[1]).resolve()
    code = Path('tests/CliSmoke/Model.cs').resolve()
    rejected = subprocess.run(['dotnet', str(host), '--project', 'not-evaluated.csproj'], capture_output=True, timeout=15)
    assert rejected.returncode == 2 and b'--trust-project' in rejected.stderr
    assert not rejected.stdout, 'Host diagnostics must never pollute protocol stdout.'

    with tempfile.TemporaryFile(mode='w+b') as stderr:
        client = Client(['dotnet', str(host), '--code', str(code), '--framework', 'Portable'], stderr)
        try:
            capabilities = client.request('initialize', {'capabilities': {}})['capabilities']
            assert capabilities['positionEncoding'] == 'utf-16'
            assert capabilities['textDocumentSync']['change'] == 2
            client.send('initialized', {})
            uri = Path('tests/CliSmoke/LanguageServer.xaml').resolve().as_uri()
            source = "<View xmlns='clr-namespace:CliSmoke' Text='hello'/>"
            document = {'uri': uri}
            client.send('textDocument/didOpen', {'textDocument': {'uri': uri, 'languageId': 'xaml', 'version': 1, 'text': source}})
            assert client.diagnostics(uri, 1) == []
            position = {'line': 0, 'character': source.index('Text=')}
            params = {'textDocument': document, 'position': position}
            hover = client.request('textDocument/hover', params)
            assert 'CliSmoke.View.Text' in hover['contents']['value'], hover
            definitions = client.request('textDocument/definition', params)
            assert definitions[0]['uri'] == code.as_uri(), definitions
            highlights = client.request('textDocument/documentHighlight', params)
            assert len(highlights) == 1 and highlights[0]['range']['start'] == position
            references = client.request('textDocument/references', dict(params, context={'includeDeclaration': False}))
            assert len(references) == 1 and references[0]['uri'] == uri
            symbols = client.request('textDocument/documentSymbol', {'textDocument': document})
            assert symbols[0]['name'] == 'View'
            completion = client.request('textDocument/completion', {'textDocument': document, 'position': {'line': 0, 'character': 3}})
            assert any(item['label'] == 'View' for item in completion['items'])
            tokens = client.request('textDocument/semanticTokens/full', {'textDocument': document})['data']
            assert tokens and len(tokens) % 5 == 0 and all(value >= 0 for value in tokens)
            inspection = client.request('xamlg/inspect', {'textDocument': document})
            assert inspection['bound']['typeName'] == 'CliSmoke.View'
            assert 'global::CliSmoke.View' in inspection['generated']

            # A malformed source revision must diagnose without terminating the host.
            malformed = source.replace("'hello'", "'hello'")[:-2]
            client.send('textDocument/didChange', {'textDocument': dict(document, version=2), 'contentChanges': [{'text': malformed}]})
            assert client.diagnostics(uri, 2), 'Expected syntax errors.'

            # Full replacement followed by a sequential UTF-16 range in the SAME notification.
            unicode_source = source.replace('hello', '😀')
            value_start = unicode_source.index('😀')
            client.send('textDocument/didChange', {
                'textDocument': dict(document, version=3),
                'contentChanges': [{'text': unicode_source}, {'range': {'start': {'line': 0, 'character': value_start}, 'end': {'line': 0, 'character': value_start + 2}}, 'text': 'recovered'}]})
            assert client.diagnostics(uri, 3) == []
            inspection = client.request('xamlg/inspect', {'textDocument': document})
            assert 'recovered' in inspection['generated']
            client.send('textDocument/didClose', {'textDocument': document})
            assert client.diagnostics(uri, None) == []
            assert client.request('shutdown') is None
            client.send('exit')
            client.process.stdin.close()
            assert client.process.wait(timeout=15) == 0
            print('PASS: trust boundary, clean stdout, initialize, diagnostics, hover, definitions, references, highlights, completion, symbols, semantic tokens, inspection, malformed-source recovery, sequential UTF-16 edits, close, shutdown.')
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
