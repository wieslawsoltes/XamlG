import { cp, mkdir } from 'node:fs/promises';
await mkdir(new URL('./wwwroot/monaco/', import.meta.url), { recursive: true });
await cp(new URL('./node_modules/monaco-editor/min/vs/', import.meta.url), new URL('./wwwroot/monaco/vs/', import.meta.url), { recursive: true });
