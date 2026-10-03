import { test, expect } from '@playwright/test';

test('Run captures an edit before its debounce timer fires', async ({ page }) => {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.evaluate(() => {
    const model = monaco.editor.getModels().find(model => model.getLanguageId() === 'xml');
    model.setValue(model.getValue().replace('Made of XAML. Compiled to C#.', 'Immediate snapshot marker'));
    document.querySelector('[data-testid="run-preview"]').click();
  });
  await expect(page.locator('.statusbar')).toContainText('Preview running');
  await expect.poll(() => page.evaluate(() => monaco.editor.getModels().some(model =>
    model.getLanguageId() === 'csharp' && model.getValue().includes('Immediate snapshot marker')))).toBe(true);
  await page.getByRole('button', { name: 'Toggle color theme' }).click();
  expect(await page.evaluate(() => monaco.editor.getModels().find(model => model.getLanguageId() === 'xml').getValue())).toContain('Immediate snapshot marker');
});

test('document switching, source undo and redo use the managed revision', async ({ page }) => {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.getByLabel('Example', { exact: true }).selectOption('1');
  await expect.poll(() => page.evaluate(() => monaco.editor.getModels().find(model => model.getLanguageId() === 'xml').getValue())).toContain('CounterView');
  await page.evaluate(() => {
    const model = monaco.editor.getModels().find(model => model.getLanguageId() === 'xml');
    model.setValue(model.getValue().replace('0 clicks', 'Edited counter'));
    document.querySelector('[data-testid="run-preview"]').click();
  });
  await expect(page.locator('.statusbar')).toContainText('Preview running');
  await page.getByTitle('Undo source edit').click();
  await expect.poll(() => page.evaluate(() => monaco.editor.getModels().find(model => model.getLanguageId() === 'xml').getValue())).toContain('0 clicks');
  await page.getByTitle('Redo source edit').click();
  await expect.poll(() => page.evaluate(() => monaco.editor.getModels().find(model => model.getLanguageId() === 'xml').getValue())).toContain('Edited counter');
});
