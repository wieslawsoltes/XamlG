import { cp, mkdir, readFile, writeFile } from 'node:fs/promises';
import { patchMonacoLifetime } from './build/monaco-lifetime-patch.mjs';

// Always derive output from the untouched, pinned npm package. Re-running preparation
// is deterministic and never cumulatively patches node_modules or previously built assets.
const input = new URL('./node_modules/monaco-editor/min/vs/', import.meta.url);
const output = new URL('./wwwroot/monaco/vs/', import.meta.url);
const patched = patchMonacoLifetime(await readFile(new URL('editor/editor.main.js', input), 'utf8'));
await mkdir(output, { recursive: true });
await cp(input, output, { recursive: true });
await writeFile(new URL('editor/editor.main.js', output), patched, 'utf8');
