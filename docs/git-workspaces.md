# Git workspaces

The implementation and capability ledger is in
[Git workspaces: implementation and acceptance](git-workspaces-implementation.md).

## Current branch status

PR #24 now includes virtual and native Git engines, GitHub PAT/PKCE/device sign-in,
rotating OAuth credentials, docked Git tools/documents, Studio integration and tests.
The shared `wwwroot/git/core.mjs` dependency is still missing from the branch because
the connected write tool blocked its creation. The PR remains draft and unmerged.

The complete local source overlay passes 140 Node tests, including a real authenticated
smart-HTTPS Git roundtrip. This is not a passing test result for the incomplete remote
checkout. Razor, actual Dockyard/browser integration and registered-app OAuth consent
remain unverified. See the ledger for remaining feature-parity boundaries.

## Local companion

```sh
XAMLG_GIT_ORIGINS='https://wieslawsoltes.github.io' \
node tools/git-companion/server.mjs
```

Paste the printed loopback origin and owner credential only into a trusted Studio's
GitHub tool. Native Git is restricted to explicitly configured HTTPS hosts and private
managed repository directories. Credentials are not saved in repository configuration.
For authorization-code OAuth, configure `XAMLG_GITHUB_CLIENT_ID` and the server-only
`XAMLG_GITHUB_CLIENT_SECRET`; device sign-in additionally requires device flow enabled
on the registered application. The default callback is
`http://127.0.0.1:47831/oauth/callback`.

## Validation commands

```sh
node --test tests/git/*.test.mjs
python tests/git/browser_test.py
```

The browser fixture requires Playwright and Chromium and exercises the JS workbench,
not the production Razor/Dockyard host. Do not treat the fixture as a production
acceptance test or merge while the shared dependency is absent.
