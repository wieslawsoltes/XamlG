import { test, expect } from './studio-fixture.mjs';

const xaml = `<StackPanel xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="LoaderView">
<TextBlock Text="Handwritten loader was adapted" />
</StackPanel>`;
const code = `using Avalonia.Controls;
using Avalonia.Markup.Xaml;
public partial class LoaderView : StackPanel
{
    public LoaderView() { InitializeComponent(); InitializeComponent(); }
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}`;

async function setProject(page, source = code) {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await expect.poll(() => page.evaluate(() => {
    if (!globalThis.monaco) return false;
    const writable = monaco.editor.getEditors().filter(editor => !editor.getOption(monaco.editor.EditorOption.readOnly));
    return ['xml', 'csharp'].every(language => writable.filter(editor => editor.getModel()?.getLanguageId() === language).length === 1);
  })).toBe(true);
  await page.evaluate(({ xaml, code }) => {
    const writable = monaco.editor.getEditors().filter(editor => !editor.getOption(monaco.editor.EditorOption.readOnly));
    for (const [language, text] of [['xml', xaml], ['csharp', code]]) {
      const editors = writable.filter(editor => editor.getModel()?.getLanguageId() === language);
      if (editors.length !== 1) throw new Error(`Expected one writable ${language} editor, found ${editors.length}.`);
      editors[0].setValue(text);
      if (editors[0].getValue() !== text) throw new Error(`The ${language} editor did not retain the source buffer.`);
    }
  }, { xaml, code: source });
}

test('trusted browser composition intercepts handwritten initialization without duplicate children', async ({ page }) => {
  await setProject(page);
  await page.getByTestId('run-preview').click();
  await expect(page.locator('.statusbar')).toContainText('Preview running');
  await expect(page.locator('.runtime-error')).toHaveCount(0);
  await page.getByRole('tab', { name: 'Visual tree', exact: true }).click();
  await expect(page.locator('.inspector-body')).toContainText('TextBlock');
});

test('isolated browser composition uses the same handwritten loader adapter', async ({ page }) => {
  await setProject(page);
  await page.getByTestId('run-isolated').click();
  await expect(page.locator('.statusbar')).toContainText('Isolated preview running');
  await expect(page.locator('.runtime-error')).toHaveCount(0);
  await page.getByTestId('reset-isolation').click();
});

test('unsupported indirect loader calls surface original C# diagnostics rather than running', async ({ page }) => {
  await setProject(page, code + '\npublic static class UnsafeLoader { public static System.Action<object> Load = AvaloniaXamlLoader.Load; }');
  await page.getByRole('button', { name: 'Compile', exact: true }).click();
  await expect(page.locator('.statusbar')).toContainText('Compilation has errors');
  await page.getByRole('tab', { name: 'Problems', exact: true }).click();
  await expect(page.locator('.diagnostics')).toContainText('XG3400');
  await expect(page.locator('.diagnostics')).toContainText('Code.cs');
});
