import test from 'node:test';
import assert from 'node:assert/strict';
import { topologicalCommits, layoutCommitGraph } from '../../tools/XamlG.Playground/wwwroot/git/graph.mjs';
const c = (oid, ...parents) => ({ oid, parents, message: oid });
test('graph sorts parents after every child, regardless of input order or clock skew', () => {
    const input = [c('base'), c('left', 'base'), c('merge', 'left', 'right'), c('right', 'base')];
    assert.deepEqual(topologicalCommits(input).map(x => x.oid), ['merge', 'left', 'right', 'base']);
});
test('linear history uses one continuous lane', () => {
    const graph = layoutCommitGraph([c('a', 'b'), c('b', 'c'), c('c')]);
    assert.equal(graph.width, 1); assert.deepEqual(graph.rows.map(r => r.lane), [0, 0, 0]);
    assert.deepEqual(graph.rows.map(r => r.incoming), [false, true, true]); assert.deepEqual(graph.boundaries, []);
});
test('merge history splits and rejoins without duplicating commits', () => {
    const graph = layoutCommitGraph([c('merge', 'left', 'right'), c('left', 'base'), c('right', 'base'), c('base')]);
    assert.equal(graph.width, 2); assert.equal(graph.rows[0].edges.length, 2);
    assert.equal(graph.rows[2].edges.find(e => e.type === 'parent').to, graph.rows[3].lane);
    assert.equal(graph.rows[1].edges.filter(e => e.type === 'through').length, 1);
});
test('truncated history retains explicit parent boundaries', () => {
    const graph = layoutCommitGraph([c('tip', 'unloaded')]);
    assert.deepEqual(graph.boundaries, [{ oid: 'unloaded', lane: 0 }]);
});
test('unloaded commit records are displayed distinctly, not invented as loaded commits', () => {
    const graph = layoutCommitGraph([c('tip', 'missing'), { oid: 'missing', unloaded: true }]);
    assert.equal(graph.rows[1].unloaded, true);
});
test('duplicate parents do not create spurious lanes or inconsistent indegrees', () => {
    const graph = layoutCommitGraph([c('tip', 'base', 'base'), c('base')]);
    assert.equal(graph.width, 1); assert.equal(graph.rows[0].edges.length, 1);
});
test('malformed, cyclic and oversized graphs fail explicitly', () => {
    assert.throws(() => topologicalCommits([c('a', 'b'), c('b', 'a')]), { code: 'graph_cycle' });
    assert.throws(() => topologicalCommits([c('a'), c('a')]), { code: 'invalid_graph' });
    assert.throws(() => layoutCommitGraph([c('a', 'b', 'c')], { maxLanes: 1 }), { code: 'graph_limit' });
});
test('independent history retains stable input priority', () => {
    assert.deepEqual(topologicalCommits([c('b'), c('a'), c('c')]).map(x => x.oid), ['b', 'a', 'c']);
});
test('large reversed linear histories are ordered without recursive traversal', () => {
    const input = Array.from({ length: 10000 }, (_, i) => c(String(i), ...(i ? [String(i - 1)] : [])));
    const result = topologicalCommits(input); assert.equal(result[0].oid, '9999'); assert.equal(result.at(-1).oid, '0');
});
