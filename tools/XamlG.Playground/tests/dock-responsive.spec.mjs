import { readFile } from 'node:fs/promises';
import { test, expect } from './studio-fixture.mjs';

const css = await readFile(new URL('../wwwroot/dockyard-studio.css', import.meta.url), 'utf8');

// Use the exact inline sizing contract emitted by the pinned docking renderer.
// The full agent-layout suite independently exercises the production application.
for (const vertical of [false, true]) {
  test(`absolute ${vertical ? 'heights' : 'widths'} fit a constrained dock and restore their preferred sizes`, async ({ page }) => {
    const size = vertical ? 'height' : 'width';
    const start = vertical ? 'top' : 'left', end = vertical ? 'bottom' : 'right';
    const original = vertical ? 960 : 1440;
    const bases = vertical ? [250, 280] : [300, 450];
    const minimum = vertical ? 48 : 80;
    await page.setContent(`<style>html,body{margin:0}*{box-sizing:border-box}</style>
      <div class="dockyard-workspace" style="display:flex;flex-direction:${vertical ? 'column' : 'row'};width:${vertical ? '400' : original}px;height:${vertical ? original : '240'}px">
        <section id="leading" class="ad-pane ad-anchorable-pane" style="flex:0 0 ${bases[0]}px;min-width:80px;min-height:48px"></section>
        <div class="splitter" style="flex:0 0 6px"></div>
        <section id="document" class="ad-pane ad-document-pane" style="flex:1 1 0px;min-width:80px;min-height:48px"></section>
        <div class="splitter" style="flex:0 0 6px"></div>
        <section id="trailing" class="ad-pane ad-anchorable-pane" style="flex:0 0 ${bases[1]}px;min-width:80px;min-height:48px"></section>
      </div>`);
    await page.addStyleTag({ content: css });
    const host = page.locator('.dockyard-workspace');
    const measure = () => host.evaluate((root, { size, start, end }) => {
      const bounds = root.getBoundingClientRect();
      return {
        start: bounds[start], end: bounds[end],
        panes: [...root.querySelectorAll('.ad-pane')].map(node => {
          const rect = node.getBoundingClientRect();
          return { start: rect[start], end: rect[end], size: rect[size], basis: getComputedStyle(node).flexBasis };
        }),
        splitters: [...root.querySelectorAll('.splitter')].map(node => node.getBoundingClientRect()[size])
      };
    }, { size, start, end });
    for (const available of [original, vertical ? 240 : 740, vertical ? 180 : 320, original]) {
      await host.evaluate((root, { size, available }) => { root.style[size] = `${available}px`; }, { size, available });
      const layout = await measure();
      expect(layout.panes[0].basis).toBe(`${bases[0]}px`);
      expect(layout.panes[2].basis).toBe(`${bases[1]}px`);
      for (const pane of layout.panes) {
        expect(pane.start).toBeGreaterThanOrEqual(layout.start - 1);
        expect(pane.end).toBeLessThanOrEqual(layout.end + 1);
        expect(pane.size).toBeGreaterThanOrEqual(minimum - 1);
      }
      for (const splitter of layout.splitters) expect(splitter).toBeCloseTo(6, 1);
      if (available === original) {
        expect(layout.panes[0].size).toBeCloseTo(bases[0], 1);
        expect(layout.panes[2].size).toBeCloseTo(bases[1], 1);
      }
    }
  });
}
