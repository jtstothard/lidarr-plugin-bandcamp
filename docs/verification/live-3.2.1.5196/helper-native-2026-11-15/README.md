# Mac ARM64 helper package and saved-owner restart checks

**Later HTTPS result:** [ordinary package import/status and browser cancellation passed](../helper-https-2026-11-15/README.md). The access blocker below is retained history.

Completed after the user confirmed continuing authorization for the remaining work. This is actual Mac ARM64 / .NET 8 runtime evidence; it does not convert a partial renewal into a completed one.

## Locally published package

The ordinary helper project, without a test entry point or production-source change, was published using:

```sh
dotnet publish src/Lidarr.Plugin.Bandcamp.CookiePush -c Release -r osx-arm64 --self-contained true -o /private/tmp/bandcamp-helper-osx-arm64-20261115
```

The package bundles .NET 8.0.24 and Playwright 1.64.1. With DOTNET_ROOT pointing to an absent directory and multilevel lookup disabled, its actual apphost executed --help successfully. The loaded libhostfxr, libhostpolicy and libcoreclr paths all came from the package ([library receipt](runtime-libraries.txt)). No global .NET runtime was installed or changed. This is the standalone helper apphost, never the Lidarr server apphost.

The bundle was archived to `/private/tmp/bandcamp-cookie-push-osx-arm64-20261115.tar.gz` (73136972 bytes, SHA-256 `0725afd862be721fe24351c9a3f6e09a829eb17eefc66a65b7eb94a195df095a`). A fresh extraction matched all **307** file hashes and executable modes; the extracted ordinary apphost also passed --help without a global runtime. [Full package manifest](package-manifest.json). This is a locally prepared artifact, not a notarized/signed distribution claim or a GitHub release.

Using the package's own bundled Node/Playwright CLI, Chromium was installed successfully into a fresh private browser directory:

```sh
PLAYWRIGHT_BROWSERS_PATH=/private/tmp/bandcamp-helper-native-browser-20261115 /private/tmp/bandcamp-helper-osx-arm64-20261115/.playwright/node/darwin-arm64/node /private/tmp/bandcamp-helper-osx-arm64-20261115/.playwright/package/cli.js install chromium
```

PowerShell is unnecessary for this Mac package route. A self-contained net8.0 observation runner invoked the **production** PlaywrightCookieCapture and its production default factory/session (the wrapper only records successful OpenLoginPageAsync). Its helper DLL hash exactly matched the package: `570c45c0f42e9c3abaff00513772ea7416e711c15549024afab1e8ad42ec6a5e`. Actual output: **.NET 8.0.24, Arm64, login_page_opened=true, cancelled=true, temporary_profiles_remaining=0**. See [result](capture-result.txt), [runner source](CaptureCheck.cs). No login or identity import was attempted by that cancellation probe. This proves production browser launch/cancellation/cleanup with the packaged runtime and fresh Chromium installation; it is not a completed ordinary-CLI HTTPS renewal.

## Live restart persistence

Fresh protected backup: `/srv/docker-state/rollback-preparation/bandcamp-helper-native-20261115T170243Z`. Online SQLite backup integrity passed. The guarded [restart script](restart-check.py) required the current DLL, same container/image, unchanged Compose/config and saved owner/indexer definitions, eleven linked target files, no active Bandcamp queue items and no active import command. It preserved an exclusive dispatch receipt and then stopped/started the service, without DLL replacement, settings writes, restoration or a download.

[Actual restart result](restart-result.json) and [dispatch phases](restart-dispatch.jsonl). Post-restart checks passed: the same container/image/DLL (`674d2de854890f625e84d0d3702d0a9cbc3f0860c9479f5eee6f86aa93c26cc4`), byte-equal saved owner/indexer definitions, downloader connection test, shared-indexer connection test and one FLAC search result. All eleven POSI files remain linked and the retry flag is true. The search result is rejected for re-grab because the album is already present; no new grab was dispatched. This qualifies persistence of the **unchanged** working owner and its shared consumers, not rotation to a different identity.

## Current external blocker

The former isolated Authelia browser session contains no usable protected-origin session cookie. A fresh 1Password item lookup timed out after 30 seconds, and the canonical vault-access check also timed out after 15 seconds; that is an access blocker, not evidence that the Bandcamp account or Lidarr server failed. The user was asked asynchronously to unlock/approve 1Password while independent work continued. Full ordinary-package HTTPS renewal cannot run until the proxy login is refreshed. TLS verification has not been weakened, and no working identity was invalidated to force a changed-cookie test. Windows/Linux native packaging and natural changed-cookie rotation remain separate unproved cases. PR #55's build/control checks were green before these documentation-only changes.
