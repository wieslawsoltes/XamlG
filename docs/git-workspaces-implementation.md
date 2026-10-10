# Git workspaces: implementation and acceptance ledger

PR #24 (`feat/git-workspaces`), 10 October 2026.

## Publication and merge status

The virtual/native engines, authentication services, docked tools/documents, Studio
host integration, Node tests and browser fixture have been published in implementation
commits. The required `tools/XamlG.Playground/wwwroot/git/core.mjs` remains absent
remotely: the connected write tool blocked creation of that file. That block has not
been bypassed. The source overlay retains the supplied local dependency.

**Keep this PR draft and unmerged.** The remote checkout cannot run the Git modules
without that dependency. The 140 passing Node tests below describe the complete local
overlay, not the incomplete remote checkout or a production Studio acceptance pass.

## Virtual repositories

`VirtualRepository` separates HEAD, index and working tree; commits use only the
index. IndexedDB saves revisioned workspace state and blobs transactionally. Local
mutations serialize and check the stored revision, so another tab cannot silently
replace newer work. Lazy GitHub blobs and complete tree snapshots are hash-checked;
truncated recursive tree responses fall back to directory traversal.

Offline commits use canonical Git object encoding. Binary content, executable mode,
symlink and gitlink metadata remain distinct. File paths, refs and editing sizes are
validated. Stage/unstage, discard, rename, delete, branch checkout, stashes and selected
hunk staging preserve the HEAD/index/worktree distinction. Diff tokens reject stale
staging. BOMs, mixed line endings and missing final newlines are preserved.

Conservative diff3 merges independent same-file text edits. Competing edits remain
explicit conflicts; binary/mode conflicts are not guessed. Merge commits have both
parents. Resolution and abort retain explicit state. Cross-base stash restoration
merges the staged layer and then the working layer. Conflicts abort atomically and
retain the stash; clean restoration preserves staged versus unstaged changes.
Multiple merge bases, unrelated history and directory/file conflicts defer to native
Git rather than selecting an incorrect result.

GitHub publication uploads local commit ancestry, blobs and trees before touching
a remote ref. A persistent publication journal is saved before the ref request.
Ref updates are fast-forward-only. Uncertain responses require explicit reconciliation;
mutations are not automatically retried. Divergence preserves local work. Empty GitHub
repositories need native bootstrap before virtual branch publication.

## Native HTTPS Git companion

Run `node tools/git-companion/server.mjs` with an explicit `XAMLG_GIT_ORIGINS` value.
The companion binds to loopback, prints a generated owner credential, and requires
that credential plus exact Host/Origin validation for its JSON API. Repository roots
are private, UUID-managed directories; the HTTP API does not accept arbitrary local
repository paths or shell commands. HTTPS hosts are explicitly configured, with
`github.com` as the default. Credentials remain in process memory, not Git config.

Native Git uses argument vectors, bounded output, cancellation/timeouts, isolated
configuration and disabled hooks/submodule recursion. Managed editing rejects path
traversal, symlink/device access and stale versions. File writes are atomic replacements.

Typed operations cover init/clone, status/files, diff, whole-file and hunk staging,
commit/amend, log/show, branch management, tags, remotes, fetch, fast-forward pull,
push/explicit force-with-lease, stash/apply/pop/drop, merge, rebase, cherry-pick, revert,
sequencer continue/abort/skip, cached patch application and blame. Destructive operations
require explicit confirmation values.

A single serialized snapshot request returns status, files, refs, history and stashes.
Native diff documents expose exact HEAD/index/worktree text where it is editable text.
Hunk staging validates index and worktree versions and modes, then updates only the
index. A successful operation followed by failed status refresh is reported as completed,
not as an instruction to repeat the write. The adapter also preserves an original
operation error when subsequent refresh fails.

## GitHub authentication and API transport

The fixed-origin JSON REST/GraphQL transport supports memory-only PAT login, bounded
pagination, rate-limit metadata, cancellation and no redirect-following for credentialed
requests. A failed replacement login preserves the existing account. Sign-out cannot
be undone by an in-flight validation response.

Companion-assisted authorization-code OAuth uses S256 PKCE, expiring single-use state,
a fixed callback and a server-side secret. Device authorization uses origin-bound
opaque handles, provider polling intervals, slow-down backoff, expiration, cancellation
and single-flight polling. Only the user code and fixed GitHub verification link are
shown to the browser; device codes and OAuth credentials remain in the companion.

Memory-only access/refresh leases refresh once under concurrent requests and verify
that the authenticated user identity has not changed. Both rotating credentials are
replaced. An uncertain refresh invalidates the old lease rather than reusing a possibly
consumed refresh token. Device refresh does not send a client secret. Non-expiring PATs
remain supported. Refresh occurs before a protected request, never by blindly replaying
an API mutation after an ambiguous failure.

Configure `XAMLG_GITHUB_CLIENT_ID` and, for authorization-code flow,
`XAMLG_GITHUB_CLIENT_SECRET`. Enable device flow on the registered application to use
that sign-in option. At the default port the callback is
`http://127.0.0.1:47831/oauth/callback`. Actual registered-app consent and browser local
network permissions still require acceptance testing.

Primary protocol reference:
https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps

The API console provides JSON REST access and GraphQL via POST `/graphql`, plus
repository shortcuts. It is not a complete UI for every GitHub endpoint, binary media
type, upload host, API version or Enterprise installation.

## Docked Studio workbench

Five closed-by-default tools are registered in Dockyard: Repositories, Changes,
Branches/Stashes, History and GitHub. Git is available from the main toolbar and
Tools menu. Files, diffs, commits, commit-file comparisons and output use separate
Git document identities instead of replacing compiler source documents. Existing
compiler reconciliation does not own or retire these Git document identities.

The JS workbench shares repository/session state across tools, with independently
owned component interop facades. Commit history uses stable child-before-parent
ordering, merge lanes and explicit unloaded ancestry boundaries. Graph nodes and
lane counts are bounded; malformed/cyclic graphs fail explicitly.

Text editors retain exact source buffers. A save marks only the submitted snapshot
clean, leaving later keystrokes dirty. Late reloads and Studio captures cannot overwrite
new edits or a newer load. Closing a view retains its dirty buffer in the session;
unload warns while unsaved buffers exist. This is not a crash-persistent unsaved-editor
journal. Binary files use a bounded read-only preview.

Copy into Studio and Capture Studio edits are explicit per-file transfers. They share
Studio's automation gate/busy boundary; imports cancel queued execution before awaiting
the gate and disable automatic compilation and preview. Repository import does not
execute code, restore a solution or implicitly load project dependencies.

Pane asset-loading failures are rendered within the Git pane. Git cleanup failures
do not prevent remaining Studio service disposal. Host retirement clears browser-held
credentials; companion credentials are separate process state cleared by explicit
sign-out, expiry or companion shutdown.

## Executed validation and remaining gates

`node --test tests/git/*.test.mjs`: **140 passed, 0 failed, 0 skipped** on Linux,
Node 22.16.0 and Git 2.47.3. Tests include real native Git, an authenticated TLS
`git http-backend` clone/fetch/fast-forward-pull/push roundtrip, canonical object
comparisons, exact diffs, graph ordering, virtual merge/stash behavior, stale editors,
authentication races, token rotation, device polling and companion authorization.
GitHub API and OAuth provider responses are mocked in their protocol tests; no live
GitHub OAuth consent or production GitHub push acceptance is claimed.

A Linux/macOS Node test workflow and the local-resource Playwright browser fixture
are included. Chromium navigation in the editing environment failed with
`ERR_BLOCKED_BY_ADMINISTRATOR`; browser/IndexedDB/Monaco acceptance did not pass.
A .NET SDK was unavailable, so Razor compilation and actual Dockyard integration
were not executed locally. These are distinct gates, not covered by Node tests.

Full Fork parity remains unfinished: visual interactive rebase, line-range staging,
complete signing/LFS/submodule support, richer rename/copy/remote-management UI,
gitignore-aware virtual discovery, conflict-capable cross-base stash resolution UI,
all GitHub API/Enterprise variants, and arbitrary cloned solution/MSBuild compilation.
The advanced native UI is a typed operation form, not a complete graphical workflow
for every native command. Exact-head build and production-browser acceptance remain
required before any merge.
