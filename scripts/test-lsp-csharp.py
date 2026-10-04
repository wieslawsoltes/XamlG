#!/usr/bin/env python3
"""Unsaved C# is part of the same source snapshot used for XAML binding and rename.
The process must never persist client buffers or refactoring results to disk.
"""
import importlib.util
from pathlib import Path
import sys
import tempfile


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


client_module = load('client', 'test-lsp-host.py')
features = load('features', 'test-lsp-features.py')


def main():
    host = Path(sys.argv[1]).resolve()
    with tempfile.TemporaryDirectory() as directory, tempfile.TemporaryFile(mode='w+b') as stderr:
        root = Path(directory)
        path = root / 'Model.cs'
        code = '''using System.Collections.Generic; using XamlG.Runtime;
namespace Model {
 public class Panel { [Content] public List<object> Children {get;} = new(); }
 public class Item { public string Text {get;set;} }
 public partial class View : Panel { public string Read() => target.Text; }
}'''
        path.write_text(code)
        source = "<Panel xmlns='clr-namespace:Model' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Class='Model.View'><Item x:Name='target' Text='initial'/></Panel>"
        uri = (root / 'View.xaml').as_uri()
        client = client_module.Client(['dotnet', str(host), '--code', str(path), '--framework', 'Portable', '--no-watch'], stderr)
        try:
            client.request('initialize', {'capabilities': {'workspace': {'workspaceEdit': {'documentChanges': True}}}})
            client.send('initialized', {})
            client.send('textDocument/didOpen', {'textDocument': {'uri': uri, 'languageId': 'xaml', 'version': 1, 'text': source}})
            assert client.diagnostics(uri, 1) == []
            # An unsaved extra usage moves every later source offset; rename must edit this buffer, not disk.
            unsaved = '// unsaved 😀 prefix\n' + code.replace('target.Text;', 'target.Text + nameof(target);')
            client.send('textDocument/didOpen', {'textDocument': {'uri': path.as_uri(), 'languageId': 'csharp', 'version': 8, 'text': unsaved}})
            params = {'textDocument': {'uri': uri}, 'position': {'line': 0, 'character': source.index("'target'") + 1}}
            result = client.request('textDocument/rename', dict(params, newName='renamed'))
            edits = result['documentChanges']
            cs = next(edit for edit in edits if edit['textDocument']['uri'] == path.as_uri())
            assert cs['textDocument']['version'] == 8
            rewritten = features.apply(unsaved, cs['edits'])
            assert 'renamed.Text + nameof(renamed)' in rewritten and rewritten.startswith('// unsaved 😀 prefix')
            client.send('textDocument/didChange', {'textDocument': {'uri': path.as_uri(), 'version': 9}, 'contentChanges': [{'text': unsaved.replace('string Text', 'int Count')}]})
            failed = client.receive(lambda m: m.get('method') == 'textDocument/publishDiagnostics' and m['params']['uri'] == uri and any(d['code'] == 'XG1005' for d in m['params']['diagnostics']))
            assert failed['params']['version'] == 1
            # Closing discards the unsaved C# overlay; the immutable loaded compilation is still intact.
            client.send('textDocument/didClose', {'textDocument': {'uri': path.as_uri()}})
            inspection = client.request('xamlg/inspect', {'textDocument': {'uri': uri}})
            assert 'global::Model.Item' in inspection['generated']
            assert path.read_text() == code
            client.request('shutdown'); client.send('exit'); client.process.stdin.close()
            assert client.process.wait(timeout=15) == 0
            print('PASS: unsaved C# symbol binding, UTF-16 source offsets, versioned rename, unchanged XAML version, close recovery and no disk writes.')
        except Exception:
            stderr.seek(0)
            print(stderr.read().decode('utf-8', errors='replace'), file=sys.stderr)
            raise
        finally:
            if client.process.poll() is None:
                client.process.kill(); client.process.wait(timeout=5)


if __name__ == '__main__':
    main()
