import { randomBytes } from 'node:crypto';
import { GitError, requireValue } from '../XamlG.Playground/wwwroot/git/core.mjs';
import { boundedText } from '../XamlG.Playground/wwwroot/git/github.mjs';

/** Explicit, origin-bound device flow; provider secrets are never returned to the browser. */
export class DeviceOAuthBroker {
    #sessions = new Map(); #generation = 0; #starting = 0;
    constructor({ clientId, fetcher = globalThis.fetch, onToken, clock = Date.now }) {
        this.clientId = clientId; this.fetcher = fetcher; this.onToken = onToken; this.clock = clock;
    }
    clear() { this.#generation++; this.#sessions.clear(); }
    #prune() { for (const [id, session] of this.#sessions) if (session.expiresAt <= this.clock()) this.#sessions.delete(id); }
    #session(handle, origin) {
        this.#prune(); const session = this.#sessions.get(handle);
        requireValue(session && session.origin === origin, 'oauth_session', 'Device sign-in is missing, expired or belongs to another origin.');
        return session;
    }
    #public(session) { return { handle: session.handle, status: session.status, user: session.user ?? null,
        userCode: session.userCode, verificationUri: 'https://github.com/login/device', expiresAt: session.expiresAt,
        retryAfter: Math.max(0, Math.ceil((session.nextPollAt - this.clock()) / 1000)), error: session.error ?? null }; }
    status(handle, origin) { return this.#public(this.#session(handle, origin)); }
    cancel(handle, origin) { this.#session(handle, origin); this.#sessions.delete(handle); }
    async #post(path, body) {
        const response = await this.fetcher(`https://github.com${path}`, { method: 'POST', headers: {
            Accept: 'application/json', 'Content-Type': 'application/x-www-form-urlencoded'
        }, body: new URLSearchParams(body), redirect: 'error', credentials: 'omit', cache: 'no-store', signal: AbortSignal.timeout(30000) });
        requireValue(response.ok, 'oauth_device', 'GitHub rejected the device authorization request.');
        return JSON.parse(await boundedText(response, 65536));
    }
    async begin(origin) {
        requireValue(this.clientId, 'oauth_not_configured', 'Configure the GitHub OAuth application ID and enable device flow.');
        this.#prune(); requireValue(this.#sessions.size + this.#starting < 16, 'oauth_limit', 'Too many pending sign-ins.');
        const generation = this.#generation; this.#starting++;
        try {
            const result = await this.#post('/login/device/code', { client_id: this.clientId, scope: 'repo read:user user:email offline_access' });
            requireValue(generation === this.#generation, 'oauth_canceled', 'This sign-in was canceled.');
            requireValue(!result.error && typeof result.device_code === 'string' && result.device_code.length >= 8 && result.device_code.length <= 1024 &&
                typeof result.user_code === 'string' && /^[A-Za-z0-9-]{4,32}$/.test(result.user_code) && result.verification_uri === 'https://github.com/login/device', 'oauth_device', 'GitHub returned invalid device authorization data.');
            requireValue(Number.isSafeInteger(result.expires_in) && result.expires_in > 0 && result.expires_in <= 1800 &&
                Number.isSafeInteger(result.interval) && result.interval > 0 && result.interval <= 60, 'oauth_device', 'GitHub returned invalid device polling limits.');
            const session = { handle: randomBytes(32).toString('base64url'), origin, generation, code: result.device_code, userCode: result.user_code,
                expiresAt: this.clock() + result.expires_in * 1000, interval: result.interval * 1000,
                nextPollAt: this.clock() + result.interval * 1000, status: 'pending', pending: null };
            this.#sessions.set(session.handle, session); return this.#public(session);
        } finally { this.#starting--; }
    }
    async poll(handle, origin) {
        const session = this.#session(handle, origin);
        if (session.pending) return session.pending;
        if (session.status !== 'pending' || this.clock() < session.nextPollAt) return this.#public(session);
        session.nextPollAt = this.clock() + session.interval;
        const pending = this.#poll(session).finally(() => { session.pending = null; });
        session.pending = pending; return pending;
    }
    async #poll(session) {
        const current = () => session.generation === this.#generation && this.#sessions.get(session.handle) === session && session.expiresAt > this.clock();
        try {
            const result = await this.#post('/login/oauth/access_token', { client_id: this.clientId,
                device_code: session.code, grant_type: 'urn:ietf:params:oauth:grant-type:device_code' });
            requireValue(current(), 'oauth_canceled', 'This sign-in was canceled.');
            if (result.error === 'authorization_pending' || result.error === 'slow_down') {
                if (result.error === 'slow_down') {
                    const suggested = Number.isSafeInteger(result.interval) && result.interval > 0 && result.interval <= 1800 ? result.interval * 1000 : 0;
                    session.interval = Math.max(session.interval + 5000, suggested);
                }
                session.nextPollAt = this.clock() + session.interval; return this.#public(session);
            }
            requireValue(!result.error && typeof result.access_token === 'string', 'oauth_device_denied', 'Device authorization failed or was denied. Start a new sign-in.');
            session.user = await this.onToken(result, current);
            requireValue(current(), 'oauth_canceled', 'This sign-in was canceled.');
            session.status = 'authorized'; session.code = ''; return this.#public(session);
        } catch (error) {
            session.status = 'failed'; session.error = error.code ?? 'oauth_device_uncertain'; session.code = '';
            if (error instanceof GitError) throw error;
            throw new GitError('oauth_device_uncertain', 'The authorization response could not be confirmed. Start a new sign-in.');
        }
    }
}
