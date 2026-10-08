// Failure evidence for source/history regressions. This runs only in the test browser.
export async function captureEditorState(page, label) {
  if (!label.startsWith('failed:') && !process.env.XAMLG_TEST_VERBOSE) return null;
  const state = await page.evaluate(() => {
    let draft;
    try { draft = JSON.parse(localStorage.getItem('xamlg.draft') ?? 'null'); } catch { }
    const text = value => typeof value === 'string' ? { text: value.slice(0, 4000), length: value.length, truncated: value.length > 4000 } : value;
    const files = value => value && Object.fromEntries(Object.entries(value).slice(0, 32).map(([path, value]) => [path, text(value)]));
    return {
      status: document.querySelector('.statusbar')?.innerText,
      dialogs: [...document.querySelectorAll('[role="dialog"]')].map(node => node.innerText),
      resource: document.querySelector('select[aria-label="Project resource"]')?.value,
      draft: draft && { version: draft.version, xaml: text(draft.xaml), code: text(draft.code), resources: files(draft.resources), codeFiles: files(draft.codeFiles) },
      models: globalThis.monaco?.editor.getModels().slice(0, 32).map(model => ({
        uri: model.uri.toString(), language: model.getLanguageId(),
        ...text(model.getValue()), eol: model.getEOL(), version: model.getVersionId()
      }))
    };
  });
  console.log('EDITOR-STATE ' + label + ' ' + JSON.stringify(state));
  return state;
}
