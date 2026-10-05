import { test, expect } from '@playwright/test';

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
  await page.evaluate(({ xaml, code }) => {
    monaco.editor.getModels().find(m => m.getLanguageId() === 'xml').setValue(xaml);
    monaco.editor.getModels().find(m => m.getLanguageId() === 'csharp').setValue(code);
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
  await expect(page.locator('.diagnostics')).toContainText('XG3400');
  await expect(page.locator('.diagnostics')).toContainText('Code.cs');
});
