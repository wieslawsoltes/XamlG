import test from 'node:test';
import assert from 'node:assert/strict';
import { GitHubClient, CompanionClient, boundedText } from '../../tools/XamlG.Playground/wwwroot/git/github.mjs';
const response = (value, status = 200, headers = {}) => new Response(JSON.stringify(value), { status, headers });
test('PAT validation uses only api.github.com and never follows redirects', async () => { const requests = [], client = new GitHubClient(async (url, options) => { requests.push({ url, options }); return response({ login: 'owner' }); }); await client.signIn('test-token'); assert.equal(client.user.login, 'owner'); assert.equal(requests[0].url, 'https://api.github.com/user'); assert.equal(requests[0].options.headers.Authorization, 'Bearer test-token'); assert.equal(requests[0].options.redirect, 'error'); assert.equal(requests[0].options.credentials, 'omit'); for (const path of ['https://evil.test/', '//evil.test/', '/\\evil.test', '/user#fragment']) await assert.rejects(client.request('GET', path)); });
test('failed replacement login preserves the existing authenticated user', async () => { const client = new GitHubClient(async (_, o) => o.headers.Authorization === 'Bearer valid-token' ? response({ login: 'owner' }) : response({ message: 'bad token' }, 401)); await client.signIn('valid-token'); await assert.rejects(client.signIn('invalid-token')); assert.equal(client.user.login, 'owner'); assert.equal((await client.request('GET', '/user')).login, 'owner'); });
test('sign-out cannot be undone by an in-flight token validation', async () => { let resolve; const client = new GitHubClient(() => new Promise(r => resolve = r)); const pending = client.signIn('test-token'); client.signOut(); resolve(response({ login: 'owner' })); await assert.rejects(pending, { code: 'stale_auth' }); assert.equal(client.authenticated, false); assert.equal(client.user, null); });
test('pagination checks every next-link origin', async () => { let call = 0; const client = new GitHubClient(async () => { call++; return response([{ id: call }], 200, { link: '<https://evil.test/next>; rel="next"' }); }); await assert.rejects(client.list('/user/repos'), { code: 'unsafe_pagination' }); assert.equal(call, 1); });
test('pagination is bounded and rate-limit metadata is retained', async () => { const client = new GitHubClient(async url => response([{ id: url }], 200, { link: '<https://api.github.com/user/repos?page=2>; rel="next"', 'x-ratelimit-remaining': '9' })); await assert.rejects(client.list('/user/repos', { limit: 1 }), { code: 'pagination_limit' }); assert.equal(client.rateLimit.remaining, '9'); });
test('GraphQL errors preserve partial data explicitly', async () => { const client = new GitHubClient(async () => response({ data: { viewer: null }, errors: [{ message: 'Denied' }] })); await assert.rejects(client.graphql('query { viewer { login } }'), e => e.code === 'graphql_error' && e.details.viewer === null); });
test('response limits cancel oversized streams', async () => { const large = new Response('too big'); await assert.rejects(boundedText(large, 3), { code: 'response_limit' }); });
test('companion URL validation rejects credential URLs and remote plaintext', () => { for (const url of ['http://remote.test', 'https://u:p@remote.test', 'https://remote.test/path', 'https://remote.test/?token=bad']) assert.throws(() => new CompanionClient(url, 'owner')); assert.equal(new CompanionClient('http://127.0.0.1:47831', 'owner').origin, 'http://127.0.0.1:47831'); });

test('HEAD requests preserve status without requiring a JSON body', async () => {
    const client = new GitHubClient(async (_url, options) => { assert.equal(options.method, 'HEAD'); return new Response(null, { status: 204 }); });
    const result = await client.response('HEAD', '/user'); assert.equal(result.status, 204); assert.equal(result.data, null);
});
test('companion broker propagates rate limits without browser credentials', async () => {
    const client = new GitHubClient(() => { throw new Error('Direct fetch must not run.'); });
    client.useBroker({ call: async (action, args) => { assert.equal(action, 'github/api'); assert.equal(args.path, '/user'); return { data: { login: 'owner' }, rateLimit: { remaining: '7' } }; } });
    assert.equal((await client.request('GET', '/user')).login, 'owner'); assert.equal(client.rateLimit.remaining, '7');
});
