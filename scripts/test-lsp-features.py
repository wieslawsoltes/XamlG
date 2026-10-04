#!/usr/bin/env python3
"""Exercise editing and incremental-token features over the actual stdio transport.

No edit request may write files. The client owns applying WorkspaceEdit results.
"""
import importlib.util
import json
from pathlib import Path
import sys
import tempfile

spec = importlib.util.spec_from_file_location('lsp_client', Path(__file__).with_name('test-lsp-host.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def offset(source, position):
    lines = source.splitlines(keepends=True)
    if position['line'] == len(lines) and position['character'] == 0:
        return len(source)
    prefix = sum(len(line) for line in lines[:position['line']])
    content = lines[position['line']].encode('utf-16-le')
    return prefix + len(content[:2 * position['character']].decode('utf-16-le'))


def apply(source, edits):
    result = source
    spans = [(offset(source, edit['range']['start']), offset(source, edit['range']['end']), edit['newText']) for edit in edits]
    end = -1
    for start, stop, _ in sorted(spans):
        assert start >= end and stop >= start, 'Edits must be ordered without overlapping ranges.'
        end = stop
    for start, stop, text in sorted(spans, reverse=True):
        result = result[:start] + text + result[stop:]
    return result


def main():
    host = Path(sys.argv[1]).resolve()
    with tempfile.TemporaryDirectory() as folder, tempfile.TemporaryFile(mode='w+b') as stderr:
        root = Path(folder)
        code_file = root / 'Model.cs'
        code = '''using System.Collections.Generic; using XamlG.Runtime;
namespace Model {
 public class Panel { [Content] public List<object> Children {get;} = new(); }
 public class Item { public object Target {get;set;} public string Text {get;set;} }
 public partial class View : Panel { public string Read() => target.Text + nameof(target) + "target"; }
}'''
        code_file.write_text(code)
        source = '''<Panel xmlns='clr-namespace:Model' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Class='Model.View'><Item x:Name='target' Text='😀'/><Item Target="{x:Reference t&#97;rget}" Text='keep &amp; preserve'/><!-- untouched --></Panel>'''
        path = root / 'View.xaml'; path.write_text(source)
        uri = path.as_uri(); document = {'uri': uri}
        client = module.Client(['dotnet', str(host), '--code', str(code_file), '--framework', 'Portable', '--no-watch'], stderr)
        try:
            capabilities = client.request('initialize', {'capabilities': {'workspace': {'workspaceEdit': {'documentChanges': True}}}})['capabilities']
            assert capabilities['renameProvider']['prepareProvider']
            assert capabilities['documentFormattingProvider'] and capabilities['documentRangeFormattingProvider']
            assert capabilities['semanticTokensProvider']['full']['delta'] and capabilities['semanticTokensProvider']['range']
            assert capabilities['workspaceSymbolProvider'] and capabilities['documentLinkProvider']['resolveProvider'] is False
            client.send('initialized', {})
            client.send('textDocument/didOpen', {'textDocument': dict(document, languageId='xaml', version=1, text=source)})
            assert client.diagnostics(uri, 1) == []
            position = {'line': 0, 'character': len(source[:source.index('t&#97;')].encode('utf-16-le')) // 2}
            parameters = {'textDocument': document, 'position': position}
            prepared = client.request('textDocument/prepareRename', parameters)
            assert prepared['placeholder'] == 'target'
            definitions = client.request('textDocument/definition', parameters)
            assert definitions[0]['uri'] == uri and offset(source, definitions[0]['range']['start']) == source.index("'target'") + 1
            references = client.request('textDocument/references', dict(parameters, context={'includeDeclaration': False}))
            assert len(references) == 3 and sum(reference['uri'] == code_file.as_uri() for reference in references) == 2
            renamed = client.request('textDocument/rename', dict(parameters, newName='renamed'))
            changes = renamed['documentChanges']; assert len(changes) == 2
            xaml_edit = next(change for change in changes if change['textDocument']['uri'] == uri)
            code_edit = next(change for change in changes if change['textDocument']['uri'] == code_file.as_uri())
            assert xaml_edit['textDocument']['version'] == 1 and code_edit['textDocument']['version'] is None
            assert "x:Name='renamed'" in apply(source, xaml_edit['edits'])
            assert "{x:Reference renamed}" in apply(source, xaml_edit['edits'])
            assert 'renamed.Text + nameof(renamed) + "target"' in apply(code, code_edit['edits'])
            assert code_file.read_text() == code and path.read_text() == source, 'Rename must never mutate disk behind the client.'
            tokens = client.request('textDocument/semanticTokens/full', {'textDocument': document})
            noop = client.request('textDocument/semanticTokens/full/delta', {'textDocument': document, 'previousResultId': tokens['resultId']})
            assert noop['edits'] == []
            formatting = client.request('textDocument/formatting', {'textDocument': document, 'options': {'tabSize': 2, 'insertSpaces': True}})
            formatted = apply(source, formatting)
            assert '\n  <Item' in formatted and 't&#97;rget' in formatted and '<!-- untouched -->' in formatted
            client.send('textDocument/didChange', {'textDocument': dict(document, version=2), 'contentChanges': [{'text': formatted}]})
            assert client.diagnostics(uri, 2) == []
            delta = client.request('textDocument/semanticTokens/full/delta', {'textDocument': document, 'previousResultId': tokens['resultId']})
            rebuilt = list(tokens['data'])
            for edit in sorted(delta['edits'], key=lambda edit: edit['start'], reverse=True):
                rebuilt[edit['start']:edit['start'] + edit['deleteCount']] = edit.get('data', [])
            actual = client.request('textDocument/semanticTokens/full', {'textDocument': document})
            assert rebuilt == actual['data']
            unknown = client.request('textDocument/semanticTokens/full/delta', {'textDocument': document, 'previousResultId': 'foreign'})
            assert unknown['data'] == actual['data']
            partial = client.request('textDocument/semanticTokens/range', {'textDocument': document, 'range': {'start': {'line': 1, 'character': 0}, 'end': {'line': 2, 'character': 0}}})
            assert partial['data'] and len(partial['data']) % 5 == 0 and partial['data'][0] == 1
            assert client.request('textDocument/formatting', {'textDocument': document, 'options': {'tabSize': 2, 'insertSpaces': True}}) == []
            symbols = client.request('workspace/symbol', {'query': 'target'})
            assert any(symbol['name'] == 'target' for symbol in symbols)
            broken = "<Item xmlns='clr-namespace:Model' Txet='keep &amp; preserve'/>"
            client.send('textDocument/didChange', {'textDocument': dict(document, version=3), 'contentChanges': [{'text': broken}]})
            assert client.diagnostics(uri, 3)
            point = broken.index('Txet')
            actions = client.request('textDocument/codeAction', {'textDocument': document, 'range': {'start': {'line': 0, 'character': point}, 'end': {'line': 0, 'character': point+4}}, 'context': {'diagnostics': [], 'only': ['quickfix']}})
            assert len(actions) == 1 and actions[0]['isPreferred']
            fixed = apply(broken, actions[0]['edit']['documentChanges'][0]['edits'])
            assert "Text='keep &amp; preserve'" in fixed
            client.send('textDocument/didClose', {'textDocument': document})
            client.request('shutdown'); client.send('exit'); client.process.stdin.close()
            assert client.process.wait(timeout=15) == 0
            print('PASS: versioned rename, code-behind symbol edits, no implicit writes, name definitions/references, formatting/idempotence, code actions, workspace symbols, semantic full/delta/range and shutdown.')
        except Exception:
            stderr.seek(0); print(stderr.read().decode('utf-8', errors='replace'), file=sys.stderr); raise
        finally:
            if client.process.poll() is None:
                client.process.kill(); client.process.wait(timeout=5)

        # Older clients receive the compatible unversioned changes dictionary.
        legacy = module.Client(['dotnet', str(host), '--code', str(code_file), '--framework', 'Portable', '--no-watch'], stderr)
        try:
            legacy.request('initialize', {'capabilities': {}})
            legacy.send('textDocument/didOpen', {'textDocument': dict(document, languageId='xaml', version=1, text=source)})
            assert legacy.diagnostics(uri, 1) == []
            result = legacy.request('textDocument/rename', dict(parameters, newName='legacyName'))
            assert 'changes' in result and 'documentChanges' not in result
            assert uri in result['changes'] and code_file.as_uri() in result['changes']
            legacy.request('shutdown'); legacy.send('exit'); legacy.process.stdin.close()
            assert legacy.process.wait(timeout=15) == 0
            print('PASS: legacy WorkspaceEdit capability negotiation.')
        finally:
            if legacy.process.poll() is None:
                legacy.process.kill(); legacy.process.wait(timeout=5)


if __name__ == '__main__':
    main()
