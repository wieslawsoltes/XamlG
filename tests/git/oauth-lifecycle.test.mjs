import test from 'node:test';
import assert from 'node:assert/strict';
import { GitHubClient, CompanionClient } from '../../tools/XamlG.Playground/wwwroot/git/github.mjs';
import { DeviceOAuthBroker } from '../../tools/git-companion/oauth-device.mjs';
import { OAuthSession } from '../../tools/git-companion/oauth-session.mjs';
import { startCompanion } from '../../tools/git-companion/server.mjs';
import { mkdtemp, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
const response = value => new Response(JSON.stringify(value));
const tokens = (suffix = 'one', extra = {}) => ({ access_token: `access-token-${suffix}`, refresh_token: `refresh-token-${suffix}`, expires_in: 3600, refresh_token_expires_in: 86400, ...extra });
const deferred = () => { let resolve; const promise = new Promise(r => resolve = r); return { promise, resolve }; };

test('enclosing OAuth cancellation prevents token installation after user validation', async () => {
    const gate = deferred(); let current = true;
    const client = new GitHubClient(() => gate.promise);
    const pending = client.signIn('candidate-token', { isCurrent: () => current });
    current = false; gate.resolve(response({ login: 'owner' }));
    await assert.rejects(pending, { code: 'stale_auth' }); assert.equal(client.authenticated, false);
});
test('refresh account mismatch cannot replace an existing account', async () => {
    let login = 'owner'; const client = new GitHubClient(async () => response({ id: login === 'owner' ? 1 : 2, login }));
    await client.signIn('valid-owner-token'); login = 'other';
    await assert.rejects(client.signIn('different-account-token', { expectedUser: client.user }), { code: 'identity_changed' });
    assert.equal(client.user.login, 'owner');
});
test('invalid user identity is rejected before replacing credentials', async () => {
    const client = new GitHubClient(async () => response({ message: 'not a user' }));
    await assert.rejects(client.signIn('test-token'), { code: 'invalid_user' }); assert.equal(client.authenticated, false);
});
function lease(options = {}) {
    let now = 100000, invalidated = 0; const installed = [], requests = [];
    const session = new OAuthSession({ clientId: 'client', clientSecret: 'server-secret', clock: () => now,
        install: async (token, current, expected) => { assert.ok(current()); installed.push({ token, expected }); return { id: 1, login: 'owner' }; },
        invalidate: () => invalidated++, fetcher: async (url, request) => { requests.push({ url, request }); return response(tokens('two')); }, ...options });
    return { session, installed, requests, advance: value => now += value, get invalidated() { return invalidated; } };
}
test('non-expiring PAT and OAuth sessions never request refresh', async () => {
    const f = lease(); await f.session.accept({ access_token: 'long-lived-token' }); f.advance(100000000); await f.session.ensure();
    assert.equal(f.requests.length, 0); assert.equal(f.session.status.expiresAt, null);
});
test('near-expiry credentials rotate once under concurrent callers', async () => {
    const f = lease(); await f.session.accept(tokens()); f.advance(3550000);
    await Promise.all(Array.from({ length: 8 }, () => f.session.ensure()));
    assert.equal(f.requests.length, 1); assert.equal(f.installed.length, 2);
    assert.equal(f.requests[0].request.body.get('refresh_token'), 'refresh-token-one');
    assert.equal(f.installed[1].expected.id, 1); assert.equal(f.session.status.refreshing, false);
    assert.doesNotMatch(JSON.stringify(f.session.status), /access-token|refresh-token|server-secret/);
    f.advance(3550000); await f.session.ensure(); assert.equal(f.requests[1].request.body.get('refresh_token'), 'refresh-token-two');
});
test('device refresh never sends a client secret', async () => {
    const f = lease({ clientSecret: undefined }); await f.session.accept(tokens(), { flow: 'device' }); f.advance(3550000); await f.session.ensure();
    assert.equal(f.requests[0].request.body.has('client_secret'), false);
});
test('unconfirmed refresh invalidates credentials and is not retried', async () => {
    let calls = 0; const f = lease({ fetcher: async () => { calls++; throw new Error('response lost'); } });
    await f.session.accept(tokens()); f.advance(3550000);
    await assert.rejects(f.session.ensure(), { code: 'oauth_refresh_uncertain' });
    await f.session.ensure(); assert.equal(calls, 1); assert.equal(f.session.status.authenticated, false); assert.equal(f.invalidated, 1);
});
test('sign-out during refresh cannot restore credentials', async () => {
    const gate = deferred(); const f = lease({ fetcher: () => gate.promise });
    await f.session.accept(tokens()); f.advance(3550000); const pending = f.session.ensure(); f.session.clear();
    gate.resolve(response(tokens('two'))); await assert.rejects(pending, { code: 'oauth_canceled' });
    assert.equal(f.installed.length, 1); assert.equal(f.session.status.authenticated, false);
});
test('failed old refresh cannot sign out a newer successful PAT', async () => {
    const gate = deferred(); const f = lease({ fetcher: () => gate.promise });
    await f.session.accept(tokens()); f.advance(3550000); const pending = f.session.ensure();
    await f.session.accept({ access_token: 'replacement-pat' }, { flow: 'pat' }); gate.resolve(response({ error: 'bad_refresh_token' }));
    await assert.rejects(pending); assert.equal(f.session.status.authenticated, true); assert.equal(f.invalidated, 0);
});
test('expired non-refreshable token is invalidated without an API request', async () => {
    const f = lease(); await f.session.accept({ access_token: 'expiring-token', expires_in: 10 }); f.advance(11000);
    await assert.rejects(f.session.ensure(), { code: 'oauth_expired' }); assert.equal(f.requests.length, 0);
});
test('malformed refresh lifetimes cannot replace a valid session', async () => {
    const f = lease(); await f.session.accept(tokens());
    await assert.rejects(f.session.accept(tokens('bad', { expires_in: -1 })), { code: 'oauth_expiry' });
    assert.equal(f.installed.length, 1); assert.equal(f.session.status.authenticated, true);
});
const deviceCode = { device_code: 'private-device-code', user_code: 'AAAA-BBBB', verification_uri: 'https://github.com/login/device', expires_in: 900, interval: 5 };
function deviceFixture(overrides = {}) {
    let now = 0; const calls = [], accepted = [], replies = [{ error: 'authorization_pending' }, { error: 'slow_down' }, tokens()];
    const device = new DeviceOAuthBroker({ clientId: 'client', clock: () => now,
        fetcher: async (url, options) => { calls.push({ url, options }); return response(url.endsWith('/device/code') ? deviceCode : replies.shift()); },
        onToken: async (result, current) => { assert.ok(current()); accepted.push(result); return { login: 'owner' }; }, ...overrides });
    return { device, calls, accepted, advance: value => now += value };
}
test('device flow respects minimum polling intervals and slow_down', async () => {
    const f = deviceFixture(), start = await f.device.begin('https://studio.test');
    assert.doesNotMatch(JSON.stringify(start), /private-device-code|access-token/);
    await f.device.poll(start.handle, 'https://studio.test'); assert.equal(f.calls.length, 1);
    f.advance(5000); assert.equal((await f.device.poll(start.handle, 'https://studio.test')).status, 'pending');
    f.advance(5000); assert.equal((await f.device.poll(start.handle, 'https://studio.test')).retryAfter, 10);
    f.advance(9000); await f.device.poll(start.handle, 'https://studio.test'); assert.equal(f.calls.length, 3);
    f.advance(1000); const result = await f.device.poll(start.handle, 'https://studio.test');
    assert.equal(result.status, 'authorized'); assert.equal(f.accepted.length, 1);
    assert.equal(f.calls[1].options.body.get('grant_type'), 'urn:ietf:params:oauth:grant-type:device_code');
    assert.equal(f.calls[1].options.body.has('client_secret'), false);
});
test('device sessions enforce origin, expiry and cancellation', async () => {
    const f = deviceFixture(), start = await f.device.begin('https://studio.test');
    await assert.rejects(f.device.poll(start.handle, 'https://other.test'), { code: 'oauth_session' });
    f.advance(900001); await assert.rejects(f.device.poll(start.handle, 'https://studio.test'), { code: 'oauth_session' });
    const next = await f.device.begin('https://studio.test'); f.device.cancel(next.handle, 'https://studio.test');
    await assert.rejects(f.device.poll(next.handle, 'https://studio.test'), { code: 'oauth_session' });
});
test('device sign-out during start rejects a late provider response', async () => {
    const gate = deferred(), f = deviceFixture({ fetcher: () => gate.promise });
    const pending = f.device.begin('https://studio.test'); f.device.clear(); gate.resolve(response(deviceCode));
    await assert.rejects(pending, { code: 'oauth_canceled' });
});
test('concurrent device polls share one token exchange', async () => {
    const gate = deferred(); let exchanges = 0;
    const f = deviceFixture({ fetcher: async url => url.endsWith('/device/code') ? response(deviceCode) : (exchanges++, gate.promise) });
    const start = await f.device.begin('https://studio.test'); f.advance(5000);
    const one = f.device.poll(start.handle, 'https://studio.test'), two = f.device.poll(start.handle, 'https://studio.test');
    gate.resolve(response(tokens())); await Promise.all([one, two]); assert.equal(exchanges, 1); assert.equal(f.accepted.length, 1);
});
test('device cancellation during exchange cannot install credentials', async () => {
    const gate = deferred(); const f = deviceFixture({ fetcher: async url => url.endsWith('/device/code') ? response(deviceCode) : gate.promise });
    const start = await f.device.begin('https://studio.test'); f.advance(5000);
    const pending = f.device.poll(start.handle, 'https://studio.test'); f.device.cancel(start.handle, 'https://studio.test');
    gate.resolve(response(tokens())); await assert.rejects(pending, { code: 'oauth_canceled' }); assert.equal(f.accepted.length, 0);
});
test('device failure is terminal instead of reusing an uncertain device code', async () => {
    let exchanges = 0; const f = deviceFixture({ fetcher: async url => { if (url.endsWith('/device/code')) return response(deviceCode); exchanges++; throw new Error('lost response'); } });
    const start = await f.device.begin('https://studio.test'); f.advance(5000);
    await assert.rejects(f.device.poll(start.handle, 'https://studio.test'), { code: 'oauth_device_uncertain' });
    f.advance(5000); assert.equal((await f.device.poll(start.handle, 'https://studio.test')).status, 'failed'); assert.equal(exchanges, 1);
});
test('device verification links cannot redirect the owner to an arbitrary site', async () => {
    const f = deviceFixture({ fetcher: async () => response({ ...deviceCode, verification_uri: 'https://other.test/login' }) });
    await assert.rejects(f.device.begin('https://studio.test'), { code: 'oauth_device' });
});
test('companion cancellation while /user is pending leaves authentication unchanged', async t => {
    const gate = deferred(), entered = deferred();
    const root = await mkdtemp(join(tmpdir(), 'xamlg-oauth-race-'));
    const service = await startCompanion({ root, port: 0, origins: ['https://studio.test'], oauthClientId: 'client', oauthClientSecret: 'secret',
        fetcher: async url => { if (url.endsWith('/user')) { entered.resolve(); return gate.promise; } return response(tokens()); } });
    t.after(async () => { await service.close(); await rm(root, { recursive: true, force: true }); });
    const client = new CompanionClient(service.address, service.token, (url, options) => fetch(url, { ...options, headers: { ...options.headers, Origin: 'https://studio.test' } }));
    const start = await client.call('oauth/start'), state = new URL(start.authorizeUrl).searchParams.get('state');
    const callback = fetch(`${service.address}/oauth/callback?state=${state}&code=test`); await entered.promise;
    await client.call('oauth/cancel', { handle: start.handle }); gate.resolve(response({ id: 1, login: 'owner' }));
    assert.equal((await callback).status, 400); assert.equal((await client.call('health')).authenticated, false);
});
