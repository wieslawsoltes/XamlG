import { GitError, LIMITS, requireValue } from './core.mjs';

/** Fixed-origin, credential-scoped REST/GraphQL transport. Tokens are memory-only. */
export class GitHubClient {
    #token = ''; #fetch; #broker = null; #authEpoch = 0;
    constructor(fetcher = globalThis.fetch) { this.#fetch = fetcher; this.user = null; this.rateLimit = null; }
    async signIn(token) {
        requireValue(typeof token === 'string' && token.trim().length >= 8 && !/[\r\n\0]/.test(token), 'invalid_token', 'Enter a GitHub personal access token.');
        const epoch = ++this.#authEpoch, candidate = new GitHubClient(this.#fetch);
        candidate.#token = token.trim();
        const user = await candidate.request('GET', '/user');
        requireValue(epoch === this.#authEpoch, 'stale_auth', 'This sign-in was canceled or superseded.');
        this.#token = candidate.#token; this.#broker = null; this.user = user; return user;
    }
    useBroker(broker) { this.#authEpoch++; this.#token = ''; this.#broker = broker; this.user = null; }
    signOut() { this.#authEpoch++; this.#token = ''; this.#broker = null; this.user = null; this.rateLimit = null; }
    get authenticated() { return !!this.#token || !!this.#broker; }
    async request(method, path, body, options = {}) { return (await this.response(method, path, body, options)).data; }
    async response(method, path, body, { signal, accept = 'application/vnd.github+json' } = {}) {
        method = method.toUpperCase();
        requireValue(['GET', 'HEAD', 'POST', 'PATCH', 'PUT', 'DELETE'].includes(method), 'invalid_method', 'Unsupported API method.');
        requireValue(typeof path === 'string' && path.startsWith('/') && !path.startsWith('//') && !/[\\\r\n\0#]/.test(path), 'invalid_api_path', 'Use a GitHub API path, not an external URL.');
        const url = new URL(path, 'https://api.github.com');
        requireValue(url.origin === 'https://api.github.com' && !url.username && !url.password, 'invalid_api_path', 'Credentials are only sent to api.github.com.');
        if (this.#broker) { const result = await this.#broker.call('github/api', { method, path: url.pathname + url.search, body, accept }, signal); this.rateLimit = result.rateLimit ?? null; return result; }
        const headers = { Accept: accept, 'X-GitHub-Api-Version': '2022-11-28' };
        if (this.#token) headers.Authorization = `Bearer ${this.#token}`;
        if (body !== undefined) headers['Content-Type'] = 'application/json';
        let response;
        try { response = await this.#fetch(url.href, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal, redirect: 'error', credentials: 'omit', cache: 'no-store' }); }
        catch (error) { if (error.name === 'AbortError') throw error; throw new GitError('network_error', 'GitHub could not be reached. A write may have completed; refresh before retrying.'); }
        const text = await boundedText(response, LIMITS.responseBytes);
        let data; try { data = text ? JSON.parse(text) : null; } catch { data = text; }
        this.rateLimit = { remaining: response.headers.get('x-ratelimit-remaining'), reset: response.headers.get('x-ratelimit-reset'), retryAfter: response.headers.get('retry-after') };
        if (!response.ok) throw new GitError(`github_${response.status}`, data?.message ?? `GitHub returned HTTP ${response.status}.`, { status: response.status, ...this.rateLimit });
        return { data, status: response.status, link: response.headers.get('link'), rateLimit: this.rateLimit };
    }
    async *pages(path, { limit = 100, signal } = {}) {
        requireValue(Number.isInteger(limit) && limit >= 1 && limit <= 1000, 'invalid_limit', 'Pagination must be bounded.');
        const visited = new Set();
        for (let page = 0; path && page < limit; page++) {
            requireValue(!visited.has(path), 'pagination_loop', 'GitHub returned a repeated pagination link.'); visited.add(path);
            const result = await this.response('GET', path, undefined, { signal }); yield result.data;
            const next = /<([^>]+)>;\s*rel="next"/.exec(result.link ?? '')?.[1]; path = null;
            if (next) { const url = new URL(next); requireValue(url.origin === 'https://api.github.com' && !url.username && !url.password, 'unsafe_pagination', 'Rejected an external pagination link.'); path = url.pathname + url.search; }
        }
        if (path) throw new GitError('pagination_limit', 'The result exceeds the configured page limit. Narrow the query.');
    }
    async list(path, options) { const result = []; for await (const page of this.pages(path, options)) { requireValue(Array.isArray(page), 'invalid_page', 'Expected a paginated array.'); result.push(...page); } return result; }
    async graphql(query, variables = {}, options) {
        requireValue(typeof query === 'string' && query.trim() && query.length <= 200000, 'invalid_query', 'Enter a bounded GraphQL query.');
        const result = await this.request('POST', '/graphql', { query, variables }, options);
        if (result.errors?.length) throw new GitError('graphql_error', result.errors.map(e => e.message).join('\n'), result.data);
        return result.data;
    }
}
export async function boundedText(response, maximum) {
    if (Number(response.headers.get('content-length') ?? 0) > maximum) { await response.body?.cancel(); throw new GitError('response_limit', 'Response exceeds the configured limit.'); }
    if (!response.body?.getReader) { const text = await response.text(); requireValue(new TextEncoder().encode(text).length <= maximum, 'response_limit', 'Response exceeds the configured limit.'); return text; }
    const reader = response.body.getReader(), chunks = []; let length = 0;
    try { for (;;) { const { done, value } = await reader.read(); if (done) break; length += value.length; requireValue(length <= maximum, 'response_limit', 'Response exceeds the configured limit.'); chunks.push(value); } }
    catch (error) { await reader.cancel().catch(() => {}); throw error; }
    finally { reader.releaseLock(); }
    const bytes = new Uint8Array(length); let offset = 0; for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; } return new TextDecoder().decode(bytes);
}
export class CompanionClient {
    #token; #fetch;
    constructor(address, token, fetcher = globalThis.fetch) {
        const url = new URL(address);
        requireValue((url.protocol === 'https:' || url.protocol === 'http:' && ['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname)) && !url.username && !url.password && url.pathname === '/' && !url.search && !url.hash, 'invalid_companion', 'Use an HTTPS or loopback companion origin.');
        this.origin = url.origin; this.#token = token; this.#fetch = fetcher;
    }
    async call(action, data = {}, signal) {
        requireValue(/^[a-z]+(?:\/[a-z]+)*$/.test(action), 'invalid_action', 'Invalid companion action.');
        const response = await this.#fetch(`${this.origin}/${action}`, { method: 'POST', headers: { Authorization: `Bearer ${this.#token}`, 'Content-Type': 'application/json' }, body: JSON.stringify(data), signal, redirect: 'error', credentials: 'omit', cache: 'no-store' });
        const text = await boundedText(response, LIMITS.responseBytes); let result;
        try { result = JSON.parse(text); } catch { throw new GitError('invalid_response', 'The Git companion returned an invalid response.'); }
        if (!response.ok) throw new GitError(result.code ?? 'companion_error', result.error ?? 'Git companion request failed.');
        return result;
    }
    dispose() { this.#token = ''; }
}
