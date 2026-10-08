import { test, expect } from './studio-fixture.mjs';

// This explicitly exercises the pinned upstream contribution that produced the
// production stack trace; ordinary editor replacement is tested separately.
test('pending word-highlighter work can be cancelled without leaking an unhandled rejection', async ({ page }) => {
  const failures = [];
  page.on('pageerror', error => failures.push(error.message));
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  const result = await page.evaluate(async () => {
    const editor = monaco.editor.getEditors().find(editor => editor.getModel()?.uri.path.endsWith('/View.axaml'));
    if (!editor) throw new Error('Main source editor missing.');
    editor.focus();
    const highlighter = editor.getContribution('editor.contrib.wordHighlighter')?.wordHighlighter;
    if (!highlighter) throw new Error('Pinned word-highlighter contribution missing.');
    editor.setPosition({ lineNumber: 1, column: 3 });
    const scheduled = highlighter.runDelayer.isTriggered();
    highlighter.runDelayer.cancel();
    // Cross a browser event-loop turn so unhandled rejections would reach pageerror.
    await new Promise(resolve => setTimeout(resolve, 0));
    return { scheduled, modelDisposed: editor.getModel().isDisposed() };
  });
  expect(result).toEqual({ scheduled: true, modelDisposed: false });
  await page.getByRole('button', { name: 'Compile', exact: true }).click();
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  expect(failures).toEqual([]);
});
