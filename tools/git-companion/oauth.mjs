import { randomBytes, createHash } from 'node:crypto';
import { GitError, requireValue } from '../XamlG.Playground/wwwroot/git/core.mjs';
import { boundedText } from '../XamlG.Playground/wwwroot/git/github.mjs';

/** Authorization-code + S256 PKCE. Secrets and resulting credentials stay in this process. */
export class OAuthBroker {
    #sessions = new Map(); #states = new Map(); #generation = 0;
    constructor({ clientId, clientSecret, redirectUri, fetcher = globalThis.fetch, onToken, clock = Date.now }) {
        this.clientId = clientId; this.clientSecret = clientSecret; this.redirectUri = redirectUri; this.fetcher = fetcher; this.onToken = onToken; this.clock = clock;
    }
    begin(origin) {
        requireValue(this.clientId && this.clientSecret && this.redirectUri, 'oauth_not_configured', 'Configure the OAuth application ID, server-side secret and callback first.');
        this.#prune(); requireValue(this.#sessions.size < 16, 'oauth_limit', 'Too many pending sign-in attempts.');
        const handle = randomBytes(32).toString('base64url'), state = randomBytes(32).toString('base64url'), verifier = randomBytes(48).toString('base64url');
        const challenge = createHash('sha256').update(verifier).digest('base64url'), session = { handle, state, verifier, origin, generation: this.#generation, expiresAt: this.clock() + 600000, status: 'pending', user: null };
        this.#sessions.set(handle, session); this.#states.set(state, session);
        const url = new URL('https://github.com/login/oauth/authorize'); url.search = new URLSearchParams({ client_id: this.clientId, redirect_uri: this.redirectUri, scope: 'repo read:user user:email offline_access', state, code_challenge: challenge, code_challenge_method: 'S256' }).toString();
        return { handle, authorizeUrl: url.href, expiresAt: session.expiresAt };
    }
    #prune() { for (const [handle, session] of this.#sessions) if (session.expiresAt <= this.clock()) { this.#sessions.delete(handle); this.#states.delete(session.state); } }
    status(handle, origin) { this.#prune(); const session = this.#sessions.get(handle); requireValue(session && session.origin === origin, 'oauth_session', 'Sign-in session is missing or expired.'); return { status: session.status, user: session.user, error: session.error, expiresAt: session.expiresAt }; }
    cancel(handle, origin) { const session = this.#sessions.get(handle); requireValue(session && session.origin === origin, 'oauth_session', 'Unknown sign-in session.'); this.#states.delete(session.state); this.#sessions.delete(handle); }
    clear() { this.#generation++; this.#states.clear(); this.#sessions.clear(); }
    async callback(state, code, providerError) {
        const session = this.#states.get(state); this.#states.delete(state);
        requireValue(session && session.expiresAt > this.clock(), 'oauth_state', 'The OAuth state is invalid, expired or already consumed.');
        const current = () => session.generation === this.#generation && this.#sessions.get(session.handle) === session && session.expiresAt > this.clock();
        try {
            requireValue(!providerError && typeof code === 'string' && code.length > 0 && code.length < 4096, 'oauth_denied', 'GitHub authorization was denied or the code is missing.');
            const response = await this.fetcher('https://github.com/login/oauth/access_token', { method: 'POST', headers: { Accept: 'application/json', 'Content-Type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams({ client_id: this.clientId, client_secret: this.clientSecret, code, code_verifier: session.verifier, redirect_uri: this.redirectUri }), redirect: 'error', signal: AbortSignal.timeout(30000) });
            requireValue(response.ok, 'oauth_exchange', 'GitHub rejected the token exchange.'); const result = JSON.parse(await boundedText(response, 65536));
            requireValue(typeof result.access_token === 'string' && result.access_token.length >= 8 && !result.error && (!result.token_type || result.token_type.toLowerCase() === 'bearer'), 'oauth_exchange', 'GitHub did not issue a valid access token.');
            requireValue(current(), 'oauth_canceled', 'This sign-in was canceled.');
            const user = await this.onToken(result.access_token, current, result);
            requireValue(current(), 'oauth_canceled', 'This sign-in was canceled.'); session.status = 'authorized'; session.user = user; return user;
        } catch (error) { session.status = 'failed'; session.error = error.code ?? 'oauth_exchange'; throw error; }
        finally { session.verifier = ''; }
    }
}
