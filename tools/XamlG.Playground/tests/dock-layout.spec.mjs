import { test, expect } from './studio-fixture.mjs';
import { openStudio } from './live-preview.mjs';

test('docked tools shrink into narrow viewports without replacing their preferred splitter sizes', async ({ page }) => {
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await openStudio(page, false);
  await page.getByTestId('agent-workbench').click();
  const agent = page.getByRole('region', { name: 'Coding agent workbench' });
  await expect(agent).toBeVisible();
  const original = await agent.boundingBox();
  for (const width of [740, 620, 1440]) {
    await page.setViewportSize({ width, height: 1000 });
    await expect.poll(() => agent.evaluate(element => {
      const rect = element.getBoundingClientRect();
      return Math.max(0, -rect.left, rect.right - innerWidth);
    }), { message: `The entire docked agent must fit the ${width}px viewport` }).toBeLessThanOrEqual(1);
    await expect.poll(() => agent.evaluate(element => {
      let overflow = 0;
      for (let node = element.parentElement; node; node = node.parentElement)
        if (node.classList.contains('ad-group')) overflow = Math.max(overflow, node.scrollWidth - node.clientWidth);
      return overflow;
    })).toBeLessThanOrEqual(2);
  }
  await expect.poll(async () => Math.abs((await agent.boundingBox()).width - original.width)).toBeLessThanOrEqual(1);
  expect(errors).toEqual([]);
});
