import { GitError, LIMITS, requireValue } from './core.mjs';

// Lines retain their terminator. CRLF and a missing final newline round-trip exactly.
export const lines = value => value.match(/[^\n]*\n|[^\n]+$/g) ?? [];
/** Myers shortest edit script, with prefix/suffix trimming and a strict memory/work budget. */
export function diffLines(before, after, budget = LIMITS.diffWork) {
    requireValue(Number.isInteger(budget) && budget >= 0, 'invalid_budget', 'Invalid diff budget.');
    const a = lines(before), b = lines(after); let prefix = 0, suffix = 0;
    while (prefix < a.length && prefix < b.length && a[prefix] === b[prefix]) prefix++;
    while (suffix < a.length - prefix && suffix < b.length - prefix && a[a.length - 1 - suffix] === b[b.length - 1 - suffix]) suffix++;
    const left = a.slice(prefix, a.length - suffix), right = b.slice(prefix, b.length - suffix);
    const beginning = a.slice(0, prefix).map(line => ({ type: 'equal', line }));
    const ending = a.slice(a.length - suffix).map(line => ({ type: 'equal', line }));
    const fallback = () => ({ edits: [...beginning, ...left.map(line => ({ type: 'delete', line })), ...right.map(line => ({ type: 'add', line })), ...ending], bounded: true });
    let frontier = new Map([[1, 0]]), work = 0; const trace = [];
    for (let d = 0; d <= left.length + right.length; d++) {
        work += frontier.size; if (work > budget) return fallback();
        trace.push(new Map(frontier));
        for (let k = -d; k <= d; k += 2) {
            if (++work > budget) return fallback();
            let x = k === -d || k !== d && (frontier.get(k - 1) ?? -Infinity) < (frontier.get(k + 1) ?? -Infinity) ? frontier.get(k + 1) ?? 0 : (frontier.get(k - 1) ?? 0) + 1;
            let y = x - k;
            while (x < left.length && y < right.length && left[x] === right[y]) { x++; y++; if (++work > budget) return fallback(); }
            frontier.set(k, x);
            if (x >= left.length && y >= right.length) {
                const edits = [];
                for (let depth = trace.length - 1; depth >= 0; depth--) {
                    const v = trace[depth], diagonal = x - y;
                    const previous = diagonal === -depth || diagonal !== depth && (v.get(diagonal - 1) ?? -Infinity) < (v.get(diagonal + 1) ?? -Infinity) ? diagonal + 1 : diagonal - 1;
                    const px = v.get(previous) ?? 0, py = px - previous;
                    while (x > px && y > py) { edits.push({ type: 'equal', line: left[x - 1] }); x--; y--; }
                    if (depth === 0) break;
                    if (x === px) { edits.push({ type: 'add', line: right[y - 1] }); y--; }
                    else { edits.push({ type: 'delete', line: left[x - 1] }); x--; }
                }
                return { edits: [...beginning, ...edits.reverse(), ...ending], bounded: false };
            }
        }
    }
    return fallback();
}
export function diffHunks(before, after, context = 3) {
    requireValue(Number.isInteger(context) && context >= 0 && context <= 100, 'invalid_context', 'Invalid context length.');
    const result = diffLines(before, after), edits = result.edits, groups = []; let i = 0;
    while (i < edits.length) {
        if (edits[i].type === 'equal') { i++; continue; }
        const start = Math.max(0, i - context); let last = i;
        while (++i < edits.length) {
            if (edits[i].type !== 'equal') last = i;
            else if (i - last > 2 * context) break;
        }
        const end = Math.min(edits.length, last + context + 1);
        groups.push([start, end]); i = end;
    }
    const oldOffsets = [0], newOffsets = [0];
    for (const edit of edits) { oldOffsets.push(oldOffsets.at(-1) + (edit.type !== 'add' ? 1 : 0)); newOffsets.push(newOffsets.at(-1) + (edit.type !== 'delete' ? 1 : 0)); }
    return { bounded: result.bounded, hunks: groups.map(([start, end], id) => ({ id, oldStart: oldOffsets[start], newStart: newOffsets[start], oldCount: oldOffsets[end] - oldOffsets[start], newCount: newOffsets[end] - newOffsets[start], edits: edits.slice(start, end) })) };
}
export function applyHunks(before, hunks, selected) {
    const source = lines(before), result = [], ids = new Set(selected); let at = 0;
    requireValue(ids.size === selected.length && [...ids].every(id => hunks.some(h => h.id === id)), 'invalid_hunk', 'Unknown or duplicated hunk.');
    for (const hunk of hunks.filter(h => ids.has(h.id))) {
        const old = hunk.edits.filter(e => e.type !== 'add').map(e => e.line);
        requireValue(hunk.oldStart >= at && source.slice(hunk.oldStart, hunk.oldStart + old.length).join('') === old.join(''), 'stale_hunk', 'The patch no longer matches the index.');
        result.push(...source.slice(at, hunk.oldStart), ...hunk.edits.filter(e => e.type !== 'delete').map(e => e.line)); at = hunk.oldStart + old.length;
    }
    return [...result, ...source.slice(at)].join('');
}
export function unifiedDiff(path, before, after) {
    const { hunks, bounded } = diffHunks(before, after);
    const header = `--- ${JSON.stringify('a/' + path)}\n+++ ${JSON.stringify('b/' + path)}\n`;
    return { bounded, hunks, patch: header + hunks.map(h => `@@ -${h.oldCount ? h.oldStart + 1 : h.oldStart},${h.oldCount} +${h.newCount ? h.newStart + 1 : h.newStart},${h.newCount} @@\n` + h.edits.map(e => (e.type === 'add' ? '+' : e.type === 'delete' ? '-' : ' ') + e.line + (e.line.endsWith('\n') ? '' : '\n\\ No newline at end of file\n')).join('')).join('') };
}
