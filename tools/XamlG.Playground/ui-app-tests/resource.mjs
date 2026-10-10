import { readFileSync } from 'node:fs';

// Mirrors UiResourceHtml's package-owned include grammar. Tests execute the exact modules
// embedded in the shipping assembly, without a dev server or network module loader.
export function loadUiResource() {
  const directory = new URL('../../../src/XamlG.IntelligentUI/Resources/', import.meta.url);
  const html = readFileSync(new URL('intelligent-ui.html', directory), 'utf8').replace(
    /\/\* @include (ui-[a-z-]+\.js) \*\//g,
    (_, name) => {
      const code = readFileSync(new URL(name, directory), 'utf8');
      if (/<\/script/i.test(code)) throw new Error('Invalid script terminator in ' + name);
      return code;
    });
  if (html.includes('@include') || html.length > 1048576) throw new Error('Invalid UI resource assembly.');
  return html;
}
