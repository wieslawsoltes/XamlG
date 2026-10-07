import { test, expect } from './studio-fixture.mjs';

const markup = `<StackPanel xmlns="https://github.com/avaloniaui"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="LoaderExample.View">
  <TextBlock Text="compiled through the shared adapter" />
</StackPanel>`;
const code = `using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
namespace LoaderExample {
  public partial class View : StackPanel {
    public View() {
      AvaloniaXamlLoader.Load(this);
      AvaloniaXamlLoader.Load(null, this);
      if (Children.Count != 1 || ((TextBlock)Children[0]).Text != "compiled through the shared adapter")
        throw new InvalidOperationException("The loader adapter did not initialize exactly once.");
    }
  }
}`;

async function ready(page) {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await expect.poll(() => page.evaluate(() => {
    if (!globalThis.monaco) return false;
    const editors = monaco.editor.getEditors().filter(editor => !editor.getOption(monaco.editor.EditorOption.readOnly));
    return editors.some(editor => editor.getModel()?.getLanguageId() === 'xml') &&
      editors.some(editor => editor.getModel()?.getLanguageId() === 'csharp');
  })).toBe(true);
}

async function setProject(page, xaml, source) {
  await page.evaluate(({ xaml, source }) => {
    const editors = monaco.editor.getEditors().filter(editor => !editor.getOption(monaco.editor.EditorOption.readOnly));
    for (const [language, value] of [['xml', xaml], ['csharp', source]]) {
      const candidates = editors.filter(editor => editor.getModel()?.getLanguageId() === language);
      if (candidates.length !== 1) throw new Error(`Expected exactly one writable ${language} editor, found ${candidates.length}.`);
      candidates[0].setValue(value);
    }
    // Capture both current buffers immediately; do not depend on debounce notifications.
    document.querySelector('[data-testid="run-preview"]').click();
  }, { xaml, source });
}

test('handwritten component loaders share initialization in trusted and isolated execution', async ({ page }) => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await ready(page);
  await setProject(page, markup, code);
  await expect(page.locator('.statusbar')).toContainText('Preview running');
  await expect(page.locator('.runtime-error')).toHaveCount(0);
  await page.getByTestId('run-isolated').click();
  await expect(page.locator('.statusbar')).toContainText('Isolated preview running');
  await expect(page.locator('iframe[title="XamlG isolated preview"]')).toHaveAttribute('sandbox', 'allow-scripts');
  await page.getByTestId('reset-isolation').click();
  expect(errors).toEqual([]);
});

test('unsupported loader method groups are diagnosed against original C# source', async ({ page }) => {
  await ready(page);
  const unsupported = code.replace('public View() {',
    'public static readonly Action<object> Unadapted = AvaloniaXamlLoader.Load;\n    public View() {');
  await setProject(page, markup, unsupported);
  await expect(page.locator('.statusbar')).toContainText('Compilation has errors');
  await page.getByRole('tab', { name: 'Problems', exact: true }).click();
  await expect(page.locator('.diagnostics')).toContainText('XG3400');
  await expect(page.locator('.diagnostics')).toContainText('Code.cs');
  await expect(page.locator('.runtime-error')).toHaveCount(0);
});
