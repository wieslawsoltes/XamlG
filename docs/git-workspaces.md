# Git workspaces

The Git workbench is being implemented on `feat/git-workspaces`. This document describes the implementation contract; it is not a claim that every item below is already available.

## Architecture

- Dockyard tool windows for repositories, changes, branches, history and GitHub. File editors, comparisons and commit details open as documents rather than replacing the compiler editor.
- A browser-persistent GitHub virtual provider supports opening a repository over HTTPS, lazy file reads, independent worktree/index snapshots, local commits and explicit fast-forward-only publication through GitHub's Git Database API.
- A private, owner-authenticated Git companion runs native Git for HTTPS clones and advanced operations. It does not evaluate arbitrary shell commands. This separates complete Git object/protocol semantics from browser CORS restrictions without sending credentials through public CORS proxies.
- A reusable GitHub REST/GraphQL client supports PAT authentication and a companion-assisted authorization-code OAuth flow with PKCE, single-use state and a fixed callback. Secrets remain outside repository data and browser persistence.
- Source synchronization with the compiler is explicit. Loading a repository does not execute code, restore automatic preview, run hooks, initialize submodules or silently discard editor changes.

## Correctness and security acceptance

Separate worktree, index and HEAD; preserve binary files and executable/symlink/gitlink metadata; reject traversal and `.git` writes; serialize mutations; detect stale editor revisions; preserve local work after network errors; reject non-fast-forward publication; never automatically retry a mutation with an uncertain result; never persist tokens in Git configuration or workspace snapshots.

Native operations use an argument vector, bounded output, cancellation/timeouts, a private repository root, disabled external hooks/configuration and explicit confirmation for destructive operations. OAuth and API requests are origin-bound and owner-authenticated; credentials are never accepted from repository content.

## Validation

The editing environment has Node and native Git but no .NET SDK. Node/native integration tests can run locally. Razor/Blazor and production-browser acceptance must be verified by the repository's actual exact-head CI before merge. Final results and capability boundaries will be recorded here and in the PR.

## Primary protocol references

- https://docs.github.com/en/rest/git
- https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps
- https://docs.github.com/en/rest/using-the-rest-api/using-pagination-in-the-rest-api
- https://git-scm.com/docs
