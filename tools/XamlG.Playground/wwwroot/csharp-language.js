// Roslyn runs in this browser. Providers are registered once and resolve their owning
// editor at invocation time, so docking/disposal cannot transfer a callback to a new file.
const { SourceBuffer } = await (globalThis.xamlgBoot?.importModule('source-buffer.js') ?? import('./source-buffer.js'));
let installed = false;
export function installCSharpLanguage(monaco, editors) {
  if (installed) return;
  installed = true;
  let pending = 0;
  const navigationModels = new Map();
  const owner = model => [...editors.values()].find(item => item.model === model && item.path);
  async function query(model, position, kind, cancellation, targetPath = null) {
    const item = owner(model);
    if (!item || model.isDisposed() || cancellation?.isCancellationRequested || pending >= 8) return null;
    const version = model.getVersionId(); pending++;
    try {
      const result = await item.dotnet.invokeMethodAsync('LanguageQuery', {
        path: item.path, text: item.source.text, offset: item.source.offsetAt(position), kind, targetPath
      });
      return cancellation?.isCancellationRequested || model.isDisposed() || owner(model) !== item || model.getVersionId() !== version ? null : result;
    } catch { return null; }
    finally { pending--; }
  }
  const range = (model, span) => {
    const source = owner(model)?.source ?? model;
    const start = source.positionAt ? source.positionAt(span.start) : model.getPositionAt(span.start);
    const end = source.positionAt ? source.positionAt(span.start + span.length) : model.getPositionAt(span.start + span.length);
    return new monaco.Range(start.lineNumber, start.column, end.lineNumber, end.column);
  };
  const locationRange = (item, source = null) => new monaco.Range(item.startLine + 1,
    source?.displayColumn(item.startLine + 1, item.startColumn + 1) ?? item.startColumn + 1, item.endLine + 1,
    source?.displayColumn(item.endLine + 1, item.endColumn + 1) ?? item.endColumn + 1);
  const kinds = monaco.languages.CompletionItemKind;
  const completionKind = { NamedType: kinds.Class, Namespace: kinds.Module, Method: kinds.Method, Property: kinds.Property,
    Field: kinds.Field, Event: kinds.Event, Local: kinds.Variable, Parameter: kinds.Variable, TypeParameter: kinds.TypeParameter, Alias: kinds.Reference };
  monaco.languages.registerCompletionItemProvider('csharp', {
    triggerCharacters: ['.'],
    async provideCompletionItems(model, position, _context, cancellation) {
      const result = await query(model, position, 'completion', cancellation);
      return { incomplete: result?.truncated ?? false, suggestions: (result?.items ?? []).map(item => ({
        label: item.label, insertText: item.insertText, detail: item.detail, kind: completionKind[item.kind] ?? kinds.Text,
        range: range(model, result.span)
      })) };
    }
  });
  monaco.languages.registerHoverProvider('csharp', {
    async provideHover(model, position, cancellation) {
      const result = await query(model, position, 'hover', cancellation);
      if (!result) return null;
      // Documentation is source content. Markdown commands and HTML are never trusted.
      const display = String(result.display ?? result.type ?? '').replaceAll('```', '\u0060\u200b\u0060\u200b\u0060');
      return { range: range(model, result.span), contents: [{ value: '```csharp\n' + display + '\n```', isTrusted: false, supportHtml: false }] };
    }
  });
  async function locations(model, position, items, cancellation) {
    const results = [];
    for (const item of items.slice(0, 1000)) {
      if (cancellation?.isCancellationRequested || model.isDisposed()) break;
      const existing = [...editors.values()].find(editor => editor.path === item.path && editor.model);
      let target = existing?.model;
      let targetSource = existing?.source;
      if (!target) {
        const source = await query(model, position, 'document', cancellation, item.path);
        if (!source || source.text.length > 1_048_576) continue;
        targetSource = new SourceBuffer(source.text, true);
        const uri = monaco.Uri.from({ scheme: 'xamlg-source', authority: 'project', path: '/' + item.path });
        target = navigationModels.get(item.path);
        if (target && target.getValue() !== source.text) target.setValue(source.text);
        if (!target) { target = monaco.editor.createModel(source.text, 'csharp', uri); navigationModels.set(item.path, target); }
        // Bound retained navigation buffers independently of the editor/source stores.
        while (navigationModels.size > 16 || [...navigationModels.values()].reduce((sum, m) => sum + m.getValueLength(), 0) > 8_388_608) {
          const [path, discarded] = navigationModels.entries().next().value;
          navigationModels.delete(path); discarded.dispose();
        }
      }
      results.push({ uri: target.uri, range: locationRange(item, targetSource) });
    }
    return results;
  }
  monaco.languages.registerDefinitionProvider('csharp', {
    async provideDefinition(model, position, cancellation) {
      const result = await query(model, position, 'definition', cancellation);
      return locations(model, position, result ?? [], cancellation);
    }
  });
  monaco.languages.registerTypeDefinitionProvider('csharp', {
    async provideTypeDefinition(model, position, cancellation) {
      return locations(model, position, await query(model, position, 'typeDefinition', cancellation) ?? [], cancellation);
    }
  });
  monaco.languages.registerImplementationProvider('csharp', {
    async provideImplementation(model, position, cancellation) {
      const result = await query(model, position, 'implementation', cancellation);
      return locations(model, position, result?.locations ?? [], cancellation);
    }
  });
  monaco.languages.registerDocumentSymbolProvider('csharp', {
    async provideDocumentSymbols(model, cancellation) {
      const result = await query(model, { lineNumber: 1, column: 1 }, 'symbols', cancellation);
      const symbols = monaco.languages.SymbolKind;
      const kinds = { Namespace: symbols.Namespace, Class: symbols.Class, Struct: symbols.Struct, Interface: symbols.Interface,
        Enum: symbols.Enum, Delegate: symbols.Function, Method: symbols.Method, Constructor: symbols.Constructor,
        Property: symbols.Property, Field: symbols.Field, Event: symbols.Event, TypeParameter: symbols.TypeParameter };
      const roots = [], byId = new Map();
      for (const item of result?.symbols ?? []) {
        if (!item.extent) continue;
        const declaration = item.locations.find(location => location.path === owner(model)?.path &&
          location.start >= item.extent.start && location.start + location.length <= item.extent.start + item.extent.length) ?? item.extent;
        const symbol = { name: item.name, detail: item.display, kind: kinds[item.kind] ?? symbols.Variable, tags: [],
          range: locationRange(item.extent, owner(model)?.source), selectionRange: locationRange(declaration, owner(model)?.source), children: [] };
        byId.set(item.id, symbol);
        const parent = byId.get(item.parentId); (parent?.children ?? roots).push(symbol);
      }
      return roots;
    }
  });
  monaco.languages.registerReferenceProvider('csharp', {
    async provideReferences(model, position, context, cancellation) {
      const result = await query(model, position, 'references', cancellation);
      return locations(model, position, (result?.locations ?? []).filter(item => context.includeDeclaration || !item.isDeclaration), cancellation);
    }
  });
  monaco.languages.registerSignatureHelpProvider('csharp', {
    signatureHelpTriggerCharacters: ['(', ','], signatureHelpRetriggerCharacters: [')'],
    async provideSignatureHelp(model, position, cancellation) {
      const result = await query(model, position, 'signature', cancellation);
      if (!result?.signatures.length) return null;
      return { dispose() {}, value: { activeSignature: Math.max(0, result.signatures.findIndex(s => s.isSelected)), activeParameter: result.activeParameter,
        signatures: result.signatures.map(s => ({ label: s.label, parameters: s.parameters.map(label => ({ label })) })) } };
    }
  });
  monaco.editor.registerEditorOpener({
    async openCodeEditor(source, resource, selection) {
      const editor = [...editors.values()].find(item => item.editor === source);
      const target = [...editors.values()].find(item => item.model?.uri.toString() === resource.toString());
      const path = target?.path ?? (resource.scheme === 'xamlg-source' ? resource.path.slice(1) : null);
      if (!editor || !path || !selection) return false;
      const targetModel = target?.model ?? monaco.editor.getModel(resource);
      if (!targetModel) return false;
      const targetSource = target?.source ?? new SourceBuffer(targetModel.getValue(undefined, true), true);
      const startLine = selection.startLineNumber ?? selection.lineNumber, endLine = selection.endLineNumber ?? startLine;
      const startColumn = selection.startColumn ?? selection.column, endColumn = selection.endColumn ?? startColumn;
      await editor.dotnet.invokeMethodAsync('Navigate', { path, startLine: startLine - 1,
        startColumn: targetSource.sourceColumn(startLine, startColumn) - 1,
        endLine: endLine - 1, endColumn: targetSource.sourceColumn(endLine, endColumn) - 1 });
      return true;
    }
  });
}
