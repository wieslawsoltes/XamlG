import { requireValue, text } from './core.mjs';

/** Exact editor text lives outside the DOM; closing a view never clears its buffer. */
export function editBuffer(buffer, value) {
    requireValue(typeof value === 'string', 'invalid_buffer', 'Editor content must be text.');
    if (value === buffer.text) return;
    buffer.text = value; buffer.editRevision = (buffer.editRevision ?? 0) + 1; buffer.dirty = true;
}

function revision(buffer) {
    return { repository: buffer.repository, path: buffer.path, version: buffer.version,
        edit: buffer.editRevision ?? 0, text: buffer.text };
}
function unchanged(buffer, previous) {
    return buffer.repository === previous.repository && buffer.path === previous.path &&
        buffer.version === previous.version && (buffer.editRevision ?? 0) === previous.edit && buffer.text === previous.text;
}

/** A successful save marks only the submitted text clean, not later keystrokes. */
export async function saveFileBuffer(buffer) {
    requireValue(buffer.loaded && !buffer.binary && typeof buffer.text === 'string', 'invalid_buffer', 'Load a text file before saving.');
    const previous = revision(buffer);
    const version = await previous.repository.write(previous.path, previous.text, { expectedOid: previous.version, mode: buffer.mode });
    requireValue(buffer.repository === previous.repository && buffer.path === previous.path && buffer.version === previous.version,
        'buffer_changed', 'The save completed but the editor moved to another file version. Reload before saving again.');
    buffer.version = version;
    buffer.dirty = buffer.text !== previous.text;
    return { version, dirty: buffer.dirty };
}

/** Reject late reads rather than overwriting edits made while a reload was in flight. */
export async function loadFileBuffer(buffer) {
    const previous = revision(buffer), request = buffer.loadRequest = (buffer.loadRequest ?? 0) + 1;
    const file = await previous.repository.read(previous.path);
    requireValue(buffer.loadRequest === request && unchanged(buffer, previous), 'stale_load', 'This buffer changed while the file was loading. The current edits were retained.');
    let value, binary = false;
    try { value = text(file.bytes); }
    catch (error) {
        if (error.code !== 'binary_file') throw error;
        binary = true; value = `Binary file · ${file.bytes.length} bytes\n${Array.from(file.bytes.slice(0, 4096), b => b.toString(16).padStart(2, '0')).join(' ')}`;
    }
    buffer.bytes = file.bytes; buffer.version = file.entry.oid; buffer.mode = file.entry.mode;
    buffer.text = value; buffer.binary = binary; buffer.loaded = true; buffer.dirty = false;
}

/** Studio capture is optimistic: a late host response cannot replace new Git edits. */
export async function captureFileBuffer(buffer, readSource) {
    const previous = revision(buffer), value = await readSource();
    requireValue(unchanged(buffer, previous), 'buffer_changed', 'This Git buffer changed during Studio capture. The current edits were retained.');
    editBuffer(buffer, value);
}
