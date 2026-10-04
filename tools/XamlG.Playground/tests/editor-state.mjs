// Failure evidence for source/history regressions. This runs only in the test browser.
export async function captureEditorState(page, label) {
  const state = await page.evaluate(() => {
    let draft;
    try { draft = JSON.parse(localStorage.getItem('xamlg.draft') ?? 'null'); } catch { }
    return {
      status: document.querySelector('.statusbar')?.innerText,
      dialogs: [...document.querySelectorAll('[role="dialog"]')].map(node => node.innerText),
      resource: document.querySelector('select[aria-label="Project resource"]')?.value,
      draft,
      models: globalThis.monaco?.editor.getModels().map(model => ({
        uri: model.uri.toString(), language: model.getLanguageId(),
        text: model.getValue(), eol: model.getEOL(), version: model.getVersionId()
      }))
    };
  });
  console.log('EDITOR-STATE ' + label + ' ' + JSON.stringify(state));
  return state;
}
