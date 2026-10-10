import { createServer } from 'node:http';
import { randomBytes, createHash, timingSafeEqual } from 'node:crypto';
import { pathToFileURL } from 'node:url';
import { resolve } from 'node:path';
import { homedir } from 'node:os';
import { GitError, LIMITS, requireValue } from '../XamlG.Playground/wwwroot/git/core.mjs';
import { GitHubClient } from '../XamlG.Playground/wwwroot/git/github.mjs';
import { NativeGit } from './native.mjs';
import { OAuthBroker } from './oauth.mjs';
import { DeviceOAuthBroker } from './oauth-device.mjs';
import { OAuthSession } from './oauth-session.mjs';
const digest = value => createHash('sha256').update(value).digest();
const send = (response, status, value) => { response.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' }); response.end(JSON.stringify(value)); };
async function json(request) {
    requireValue(/^application\/json(?:;|$)/i.test(request.headers['content-type'] ?? ''), 'content_type', 'Use application/json.');
    const chunks = []; let length = 0; for await (const chunk of request) { length += chunk.length; requireValue(length <= LIMITS.responseBytes, 'request_limit', 'Request is too large.'); chunks.push(chunk); }
    let result; try { result = JSON.parse(Buffer.concat(chunks).toString('utf8')); } catch { throw new GitError('invalid_json', 'Invalid JSON request.'); }
    requireValue(result && typeof result === 'object' && !Array.isArray(result), 'invalid_json', 'Expected a JSON object.'); return result;
}
export async function startCompanion({ root, origins, token = randomBytes(32).toString('base64url'), port = 47831, hosts = ['github.com'], caFile, oauthClientId, oauthClientSecret, fetcher = globalThis.fetch } = {}) {
    requireValue(typeof root === 'string' && Array.isArray(origins) && origins.length > 0 && typeof token === 'string' && token.length >= 32, 'configuration', 'A managed root, explicit browser origins and a strong owner credential are required.');
    for (const origin of origins) { const url = new URL(origin); requireValue(url.origin === origin && (url.protocol === 'https:' || url.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname)), 'configuration', 'Only exact HTTPS or loopback browser origins are allowed.'); }
    const ownerHash = digest(token), native = new NativeGit(root, { hosts, caFile }), github = new GitHubClient(fetcher); let address;
    const session = new OAuthSession({ clientId: oauthClientId, clientSecret: oauthClientSecret, fetcher,
        install: async (accessToken, current, expectedUser) => {
            const user = await github.signIn(accessToken, { isCurrent: current, expectedUser });
            if (hosts.includes('github.com')) native.setCredential('github.com', 'x-access-token', accessToken);
            return user;
        }, invalidate: () => { github.signOut(); native.clearCredentials(); } });
    const oauth = new OAuthBroker({ clientId: oauthClientId, clientSecret: oauthClientSecret, fetcher,
        onToken: (_token, current, result) => session.accept(result, { current }) });
    const device = new DeviceOAuthBroker({ clientId: oauthClientId, fetcher,
        onToken: (result, current) => session.accept(result, { current, flow: 'device' }) });
    const server = createServer(async (request, response) => {
        try {
            const expectedHost = new URL(address).host; requireValue(request.headers.host === expectedHost, 'invalid_host', 'Invalid companion Host header.');
            const url = new URL(request.url, address);
            if (request.method === 'GET' && url.pathname === '/oauth/callback') {
                let success = false; try { await oauth.callback(url.searchParams.get('state'), url.searchParams.get('code'), url.searchParams.get('error')); success = true; } catch {}
                response.writeHead(success ? 200 : 400, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store', 'Referrer-Policy': 'no-referrer', 'Content-Security-Policy': "default-src 'none'; frame-ancestors 'none'; base-uri 'none'", 'X-Content-Type-Options': 'nosniff' });
                response.end(`<!doctype html><title>XamlG GitHub sign-in</title><h1>${success ? 'GitHub connected' : 'Sign-in failed'}</h1><p>Return to XamlG Studio. This tab can be closed.</p>`); return;
            }
            const origin = request.headers.origin; requireValue(origins.includes(origin), 'invalid_origin', 'Browser origin is not allowed.');
            response.setHeader('Access-Control-Allow-Origin', origin); response.setHeader('Vary', 'Origin');
            if (request.method === 'OPTIONS') {
                requireValue(request.headers['access-control-request-method'] === 'POST', 'invalid_method', 'Only POST is allowed.');
                const requested = (request.headers['access-control-request-headers'] ?? '').toLowerCase().split(',').map(x => x.trim()).filter(Boolean); requireValue(requested.every(x => ['authorization', 'content-type'].includes(x)), 'invalid_headers', 'Unexpected CORS request headers.');
                response.writeHead(204, { 'Access-Control-Allow-Methods': 'POST', 'Access-Control-Allow-Headers': 'Authorization, Content-Type', 'Access-Control-Max-Age': '600' }); response.end(); return;
            }
            requireValue(request.method === 'POST', 'invalid_method', 'Use POST.');
            const authorization = request.headers.authorization ?? ''; requireValue(authorization.startsWith('Bearer ') && timingSafeEqual(digest(authorization.slice(7)), ownerHash), 'unauthorized', 'Companion owner authentication is required.');
            const body = await json(request), controller = new AbortController(); response.on('close', () => { if (!response.writableEnded) controller.abort(); }); const signal = controller.signal;
            let result;
            switch (url.pathname) {
                case '/health': result = { version: 1, authenticated: github.authenticated, oauthConfigured: !!oauthClientId && !!oauthClientSecret, deviceOAuthConfigured: !!oauthClientId, session: session.status, hosts }; break;
                case '/git/list': result = await native.list(); break;
                case '/git/init': result = await native.init(body, signal); break;
                case '/git/clone': await session.ensure(); result = await native.clone(body, signal); break;
                case '/git/run': if (['fetch', 'pull', 'push'].includes(body.operation)) await session.ensure(); result = await native.execute(body.id, body.operation, body.args, signal); break;
                case '/git/credentials': native.setCredential(body.host, body.username, body.password); result = { ok: true }; break;
                case '/github/login': { oauth.clear(); device.clear(); const user = await session.accept({ access_token: body.token }, { flow: 'pat' }); result = { user }; break; }
                case '/github/logout': oauth.clear(); device.clear(); session.clear(); result = { ok: true }; break;
                case '/github/api': await session.ensure(); result = await github.response(body.method, body.path, body.body, { signal, accept: body.accept }); break;
                case '/oauth/start': oauth.clear(); device.clear(); result = oauth.begin(origin); break;
                case '/oauth/status': result = oauth.status(body.handle, origin); break;
                case '/oauth/cancel': oauth.cancel(body.handle, origin); result = { ok: true }; break;
                case '/oauth/device/start': oauth.clear(); device.clear(); result = await device.begin(origin); break;
                case '/oauth/device/status': result = device.status(body.handle, origin); break;
                case '/oauth/device/poll': result = await device.poll(body.handle, origin); break;
                case '/oauth/device/cancel': device.cancel(body.handle, origin); result = { ok: true }; break;
                default: throw new GitError('not_found', 'Unknown companion endpoint.');
            }
            send(response, 200, result);
        } catch (error) {
            if (response.destroyed || response.headersSent) return;
            const status = error.code === 'unauthorized' ? 401 : ['invalid_origin', 'invalid_host'].includes(error.code) ? 403 : error.code === 'not_found' ? 404 : ['stale_file', 'stale_workspace', 'git_failed'].includes(error.code) ? 409 : 400;
            send(response, status, { code: error.code ?? 'companion_error', error: error instanceof GitError ? error.message : 'The companion could not complete the operation.' });
        }
    });
    server.headersTimeout = 10000; server.requestTimeout = 30000;
    await new Promise((resolvePromise, reject) => { server.once('error', reject); server.listen(port, '127.0.0.1', resolvePromise); });
    address = `http://127.0.0.1:${server.address().port}`; oauth.redirectUri = `${address}/oauth/callback`;
    return { address, token, close: async () => { oauth.clear(); device.clear(); session.clear(); server.closeIdleConnections(); await new Promise((r, reject) => server.close(error => error ? reject(error) : r())); } };
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    try {
        const service = await startCompanion({ root: process.env.XAMLG_GIT_ROOT ?? resolve(homedir(), '.xamlg-git'), origins: (process.env.XAMLG_GIT_ORIGINS ?? '').split(',').map(x => x.trim()).filter(Boolean), port: Number(process.env.XAMLG_GIT_PORT ?? 47831), token: process.env.XAMLG_GIT_OWNER_TOKEN || undefined, hosts: (process.env.XAMLG_GIT_HOSTS ?? 'github.com').split(',').map(x => x.trim()), caFile: process.env.XAMLG_GIT_CA_FILE, oauthClientId: process.env.XAMLG_GITHUB_CLIENT_ID, oauthClientSecret: process.env.XAMLG_GITHUB_CLIENT_SECRET });
        console.log(`XamlG Git companion: ${service.address}\nOwner credential (paste only into your trusted Studio): ${service.token}\nOAuth callback: ${service.address}/oauth/callback`);
        const close = () => service.close().then(() => process.exit(0)); process.once('SIGINT', close); process.once('SIGTERM', close);
    } catch (error) { console.error(error.message); process.exitCode = 1; }
}
