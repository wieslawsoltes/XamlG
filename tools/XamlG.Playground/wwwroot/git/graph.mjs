import { requireValue } from './core.mjs';

/** Stable child-before-parent ordering. Input priority is retained whenever topology permits. */
export function topologicalCommits(commits, maximum = 10000) {
    requireValue(Array.isArray(commits) && commits.length <= maximum, 'graph_limit', 'Commit graph exceeds the configured node limit.');
    const nodes = new Map(), children = new Map(), order = new Map();
    commits.forEach((commit, index) => {
        requireValue(commit && typeof commit.oid === 'string' && commit.oid && !nodes.has(commit.oid) &&
            (commit.parents === undefined || Array.isArray(commit.parents) && commit.parents.every(p => typeof p === 'string' && p)), 'invalid_graph', 'Invalid or duplicate commit graph node.');
        nodes.set(commit.oid, commit); children.set(commit.oid, 0); order.set(commit.oid, index);
    });
    for (const commit of commits) for (const parent of new Set(commit.parents ?? []))
        if (nodes.has(parent)) children.set(parent, children.get(parent) + 1);
    const heap = [], push = id => {
        heap.push(id); let i = heap.length - 1;
        while (i > 0) { const parent = (i - 1) >>> 1; if (order.get(heap[parent]) <= order.get(id)) break; heap[i] = heap[parent]; i = parent; } heap[i] = id;
    };
    const pop = () => {
        const id = heap[0], last = heap.pop(); if (!heap.length) return id;
        let i = 0;
        while (i * 2 + 1 < heap.length) {
            let next = i * 2 + 1;
            if (next + 1 < heap.length && order.get(heap[next + 1]) < order.get(heap[next])) next++;
            if (order.get(last) <= order.get(heap[next])) break;
            heap[i] = heap[next]; i = next;
        }
        heap[i] = last; return id;
    };
    for (const commit of commits) if (children.get(commit.oid) === 0) push(commit.oid);
    const result = [];
    while (heap.length) {
        const commit = nodes.get(pop()); result.push(commit);
        for (const parent of new Set(commit.parents ?? [])) if (nodes.has(parent)) {
            const count = children.get(parent) - 1; children.set(parent, count); if (!count) push(parent);
        }
    }
    requireValue(result.length === commits.length, 'graph_cycle', 'Commit ancestry contains a cycle.'); return result;
}

/** Stable graph lanes, merge edges and explicit unloaded-history boundaries. */
export function layoutCommitGraph(commits, { maximum = 10000, maxLanes = 128 } = {}) {
    requireValue(Number.isInteger(maxLanes) && maxLanes > 0 && maxLanes <= 1024, 'graph_limit', 'Invalid graph lane limit.');
    const ordered = topologicalCommits(commits, maximum), active = [], rows = [];
    let width = 1;
    const allocate = (id, preferred = -1) => {
        const present = active.indexOf(id); if (present >= 0) return present;
        let lane = preferred >= 0 && active[preferred] == null ? preferred : active.indexOf(null);
        if (lane < 0) lane = active.length;
        requireValue(lane < maxLanes, 'graph_limit', 'Commit graph exceeds the configured lane limit. Narrow the history range.');
        active[lane] = id; width = Math.max(width, active.length); return lane;
    };
    for (const commit of ordered) {
        const incoming = active.includes(commit.oid), lane = allocate(commit.oid), before = active.slice(), edges = [];
        active[lane] = null;
        for (const [index, parent] of [...new Set(commit.parents ?? [])].entries())
            edges.push({ type: 'parent', from: lane, to: allocate(parent, index === 0 ? lane : -1), oid: parent });
        for (let from = 0; from < before.length; from++) if (before[from] && before[from] !== commit.oid)
            edges.push({ type: 'through', from, to: active.indexOf(before[from]), oid: before[from] });
        while (active.length && active.at(-1) === null) active.pop();
        rows.push({ commit, lane, incoming, edges, unloaded: !!commit.unloaded });
    }
    const boundaries = active.flatMap((oid, lane) => oid ? [{ oid, lane }] : []);
    return { rows, width, boundaries };
}
