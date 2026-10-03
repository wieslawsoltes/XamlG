import { test, expect } from '@playwright/test';

const source = `<Canvas xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="White">
  <Button x:Name="shape" Canvas.Left="20" Canvas.Top="20" Width="100" Height="50" Content="Drag me" />
</Canvas>`;
const getSource = page => page.evaluate(() => monaco.editor.getModels().find(m => m.getLanguageId() === 'xml').getValue());

async function ready(page) {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.evaluate(value => {
    monaco.editor.getModels().find(m => m.getLanguageId() === 'xml').setValue(value);
    document.querySelector('[data-testid="run-preview"]').click();
  }, source);
  await expect(page.locator('.statusbar')).toContainText('Preview running');
  await page.getByTestId('design-mode').click();
  return await page.locator('#avalonia-preview').boundingBox();
}

test('real canvas drag and resize produce source transactions and reload the view', async ({ page }) => {
  const box = await ready(page);
  await page.mouse.move(box.x + 55, box.y + 45);
  await page.mouse.down();
  await page.mouse.move(box.x + 83, box.y + 73, { steps: 8 });
  await page.mouse.up();
  await expect(page.locator('.statusbar')).toContainText('Designer edit committed');
  await expect.poll(() => getSource(page)).toContain('Canvas.Left="48"');
  await expect.poll(() => getSource(page)).toContain('Canvas.Top="48"');

  // Select the reloaded control, then drag its actual bottom-right adorner.
  await page.mouse.click(box.x + 80, box.y + 70);
  await page.mouse.move(box.x + 148, box.y + 98);
  await page.mouse.down();
  await page.mouse.move(box.x + 176, box.y + 112, { steps: 8 });
  await page.mouse.up();
  await expect.poll(() => getSource(page)).toContain('Width="128"');
  await expect.poll(() => getSource(page)).toContain('Height="64"');
  await page.getByTitle('Undo source edit').click();
  await expect.poll(() => getSource(page)).toContain('Width="100"');
});

test('Escape cancels a visual gesture without changing the source', async ({ page }) => {
  const box = await ready(page);
  const before = await getSource(page);
  await page.mouse.move(box.x + 55, box.y + 45);
  await page.mouse.down();
  await page.mouse.move(box.x + 90, box.y + 80, { steps: 5 });
  await page.keyboard.press('Escape');
  await page.mouse.up();
  expect(await getSource(page)).toBe(before);
});
