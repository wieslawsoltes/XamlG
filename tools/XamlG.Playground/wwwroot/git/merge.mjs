import { diffHunks, lines } from './diff.mjs';
import { equalEntry, requireValue } from './core.mjs';

/** Conservative diff3: independent edits merge; competing edits remain explicit conflicts. */
export function mergeText(base, ours, theirs) {
    if (ours === theirs || theirs === base) return { clean: true, text: ours, conflicts: [] };
    if (ours === base) return { clean: true, text: theirs, conflicts: [] };
    const source = lines(base);
    const convert = value => diffHunks(base, value, 0).hunks.map(h => ({ start: h.oldStart, end: h.oldStart + h.oldCount,
        replacement: h.edits.filter(e => e.type !== 'delete').map(e => e.line) }));
    const left = convert(ours), right = convert(theirs), output = [], conflicts = [];
    let a = 0, b = 0, cursor = 0;
    const render = (edits, start, end) => {
        const result = []; let at = start;
        for (const edit of edits) { for (let i = at; i < edit.start; i++) result.push(source[i]); result.push(edit.replacement.join('')); at = edit.end; }
        for (let i = at; i < end; i++) result.push(source[i]); return result.join('');
    };
    while (a < left.length || b < right.length) {
        const first = !right[b] || left[a] && left[a].start <= right[b].start ? left[a] : right[b];
        const start = first.start; let end = first.end, pointAtEnd = first.start === first.end;
        const groups = [[], []];
        const take = (edit, side) => {
            groups[side].push(edit);
            if (edit.end > end) { end = edit.end; pointAtEnd = edit.start === edit.end; }
            else if (edit.start === end && edit.end === end) pointAtEnd = true;
        };
        if (first === left[a]) { take(left[a++], 0); } else { take(right[b++], 1); }
        // Boundary insertions are intentionally conservative: their ordering relative
        // to a replacement at the same boundary must not be guessed.
        const overlaps = edit => edit && (edit.start < end || edit.start === end && (edit.start === edit.end || pointAtEnd));
        for (;;) {
            let changed = false;
            while (overlaps(left[a])) { take(left[a++], 0); changed = true; }
            while (overlaps(right[b])) { take(right[b++], 1); changed = true; }
            if (!changed) break;
        }
        for (let i = cursor; i < start; i++) output.push(source[i]);
        const l = render(groups[0], start, end), r = render(groups[1], start, end);
        if (!groups[0].length) output.push(r);
        else if (!groups[1].length || l === r) output.push(l);
        else conflicts.push({ start, end, base: source.slice(start, end).join(''), ours: l, theirs: r });
        cursor = end;
    }
    for (let i = cursor; i < source.length; i++) output.push(source[i]);
    return { clean: conflicts.length === 0, text: conflicts.length ? null : output.join(''), conflicts };
}

/** Three-way tree merge; storage and binary decoding remain provider responsibilities. */
export async function mergeTrees(base, ours, theirs, { readText, writeText }) {
    const files = new Map(ours), conflicts = new Map();
    for (const path of new Set([...base.keys(), ...ours.keys(), ...theirs.keys()])) {
        const b = base.get(path), o = ours.get(path), t = theirs.get(path);
        if (equalEntry(o, t) || equalEntry(b, t)) continue;
        if (equalEntry(b, o)) { if (t) files.set(path, t); else files.delete(path); continue; }
        const mode = o?.mode === t?.mode ? o?.mode : o?.mode === b?.mode ? t?.mode : t?.mode === b?.mode ? o?.mode : null;
        if (b && o && t && [b.mode, o.mode, t.mode, mode].every(m => m === '100644' || m === '100755')) {
            try {
                const [baseText, oursText, theirsText] = await Promise.all([readText(b), readText(o), readText(t)]);
                const merged = mergeText(baseText, oursText, theirsText);
                if (merged.clean) { files.set(path, await writeText(merged.text, mode)); continue; }
            } catch (error) { if (error.code !== 'binary_file') throw error; }
        }
        conflicts.set(path, { base: b ?? null, ours: o ?? null, theirs: t ?? null });
    }
    for (const path of files.keys()) for (let at = path.indexOf('/'); at >= 0; at = path.indexOf('/', at + 1))
        requireValue(!files.has(path.slice(0, at)), 'directory_file_conflict', 'Use native Git for directory/file conflicts.');
    return { files, conflicts };
}
