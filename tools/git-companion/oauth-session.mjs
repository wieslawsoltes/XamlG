import { GitError, requireValue } from '../XamlG.Playground/wwwroot/git/core.mjs';
import { boundedText } from '../XamlG.Playground/wwwroot/git/github.mjs';

function credential(value) {
    return typeof value === 'string' && value.length >= 8 && value.length <= 16384 && !/[\s\0]/.test(value);
}
function deadline(value, now) {
    if (value === undefined) return Infinity;
    requireValue(Number.isSafeInteger(value) && value > 0 && value <= 315360000, 'oauth_expiry', 'Invalid OAuth credential lifetime.');
    return now + value * 1000;
}

/** Memory-only rotating credentials. Refreshes are single-flight and never replay API writes. */
export class OAuthSession {
    #lease = null; #epoch = 0; #pending = null;
    constructor({ clientId, clientSecret, fetcher = globalThis.fetch, install, invalidate, clock = Date.now, skew = 60000 }) {
        this.clientId = clientId; this.clientSecret = clientSecret; this.fetcher = fetcher;
        this.install = install; this.invalidate = invalidate; this.clock = clock; this.skew = skew;
    }
    get status() {
        const lease = this.#lease;
        return { authenticated: !!lease, user: lease?.user ?? null,
            expiresAt: Number.isFinite(lease?.expiresAt) ? lease.expiresAt : null,
            refreshable: !!lease?.refreshToken, refreshing: !!this.#pending };
    }
    #parse(result, flow) {
        const now = this.clock();
        requireValue(result && !result.error && credential(result.access_token) && (!result.token_type || result.token_type.toLowerCase() === 'bearer'), 'oauth_exchange', 'GitHub did not issue a valid access token.');
        requireValue(result.refresh_token === undefined || credential(result.refresh_token), 'oauth_exchange', 'GitHub returned an invalid refresh token.');
        return { token: result.access_token, refreshToken: result.refresh_token ?? null,
            expiresAt: deadline(result.expires_in, now), refreshExpiresAt: deadline(result.refresh_token_expires_in, now), flow };
    }
    async accept(result, { current = () => true, flow = 'code' } = {}) {
        const lease = this.#parse(result, flow), epoch = this.#epoch;
        const valid = () => epoch === this.#epoch && current();
        requireValue(valid(), 'oauth_canceled', 'This sign-in was canceled.');
        const user = await this.install(lease.token, valid);
        requireValue(valid(), 'oauth_canceled', 'This sign-in was canceled.');
        this.#epoch++; this.#lease = { ...lease, user }; this.#pending = null; return user;
    }
    clear() { this.#epoch++; this.#lease = null; this.#pending = null; this.invalidate(); }
    async ensure() {
        const lease = this.#lease;
        if (!lease || lease.expiresAt > this.clock() + this.skew) return;
        if (this.#pending) return this.#pending;
        if (!lease.refreshToken) {
            if (lease.expiresAt > this.clock()) return;
            this.clear(); throw new GitError('oauth_expired', 'The access token expired. Sign in again.');
        }
        if (lease.refreshExpiresAt <= this.clock()) {
            this.clear(); throw new GitError('oauth_expired', 'The refresh token expired. Sign in again.');
        }
        const epoch = this.#epoch;
        const pending = this.#refresh(lease, epoch).finally(() => { if (this.#pending === pending) this.#pending = null; });
        this.#pending = pending; return pending;
    }
    async #refresh(lease, epoch) {
        const current = () => this.#epoch === epoch && this.#lease === lease;
        try {
            requireValue(this.clientId && (lease.flow === 'device' || this.clientSecret), 'oauth_not_configured', 'OAuth refresh is not configured.');
            const body = new URLSearchParams({ client_id: this.clientId, grant_type: 'refresh_token', refresh_token: lease.refreshToken });
            if (lease.flow !== 'device') body.set('client_secret', this.clientSecret);
            const response = await this.fetcher('https://github.com/login/oauth/access_token', {
                method: 'POST', headers: { Accept: 'application/json', 'Content-Type': 'application/x-www-form-urlencoded' },
                body, redirect: 'error', credentials: 'omit', cache: 'no-store', signal: AbortSignal.timeout(30000)
            });
            requireValue(response.ok, 'oauth_refresh', 'GitHub rejected credential refresh.');
            const next = this.#parse(JSON.parse(await boundedText(response, 65536)), lease.flow);
            requireValue(next.refreshToken, 'oauth_refresh', 'GitHub did not return a replacement refresh token.');
            requireValue(current(), 'oauth_canceled', 'Credential refresh was canceled.');
            const user = await this.install(next.token, current, lease.user);
            requireValue(current(), 'oauth_canceled', 'Credential refresh was canceled.');
            this.#lease = { ...next, user }; this.#epoch++;
        } catch (error) {
            // The provider may have consumed the old refresh token. Never reuse it after
            // an uncertain exchange, and never invalidate a newer successfully installed session.
            if (current()) this.clear();
            if (error instanceof GitError) throw error;
            throw new GitError('oauth_refresh_uncertain', 'Token refresh could not be confirmed. Sign in again; the old refresh token will not be retried.');
        }
    }
}
