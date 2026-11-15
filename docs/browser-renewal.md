# User-assisted shared session renewal (#46 / #51)

Web authentication remains primary. The [live Subsonic decision](verification/subsonic-2026-11-15.md)
is NO-GO for lossless on the tested deployment; no credential lifetime is qualified. This helper does not add a login engine,
solve CAPTCHA, collect passwords, disable 2FA, or revive expired cookies by saving them.

## Desktop installation and selection

Run on your own graphical desktop, even when Lidarr runs on a headless server. Install the .NET 8
runtime and an SDK capable of building net8.0, plus PowerShell 7; clone this repository, then build the standalone helper.
A newer major runtime alone does not satisfy the helper's default net8.0 runtime selection.
Use .NET 8 for the normal invocation below; explicit major-version roll-forward requires separate runtime qualification.

```sh
dotnet build src/Lidarr.Plugin.Bandcamp.CookiePush -c Release
pwsh src/Lidarr.Plugin.Bandcamp.CookiePush/bin/Release/net8.0/playwright.ps1 install chromium
```

Linux also needs Playwright's documented Chromium OS dependencies. No desktop browser belongs
on the headless Lidarr host. The source targets Windows, macOS and Linux. A local Mac ARM64 self-contained package,
its bundled .NET 8 runtime, fresh Chromium installation and production browser capture cancellation
have now passed [native checks](verification/live-3.2.1.5196/helper-native-2026-11-15/README.md).
The [later HTTPS checks](verification/live-3.2.1.5196/helper-https-2026-11-15/README.md)
completed ordinary-package manual renewal/status and a fresh visible login-to-commit through the
production helper components with an autofill harness. Windows/Linux native execution and
Bandcamp-issued token rotation remain unqualified. The [controlled invalid-to-valid recovery test](verification/live-3.2.1.5196/changed-identity-test-2026-11-15/README.md) also passed replacement, both shared consumers and restart persistence without changing the working owner. No prebuilt helper release is claimed. The server must run this plugin version with the renewal begin/cancel/status protocol; older one-shot `renewIdentity` clients are refused.

### Mac ARM64 without a global .NET 8 runtime

An installed SDK can publish the helper with its own runtime. The bundled Playwright Node CLI
also avoids a PowerShell dependency:

```sh
dotnet publish src/Lidarr.Plugin.Bandcamp.CookiePush -c Release -r osx-arm64 --self-contained true -o artifacts/bandcamp-cookie-push/osx-arm64
artifacts/bandcamp-cookie-push/osx-arm64/.playwright/node/darwin-arm64/node artifacts/bandcamp-cookie-push/osx-arm64/.playwright/package/cli.js install chromium
artifacts/bandcamp-cookie-push/osx-arm64/bandcamp-cookie-push --help
```

Use that executable in place of `dotnet .../bandcamp-cookie-push.dll` below. Keep the whole
published directory together. The local qualification did not establish notarization, a public
release, Windows/Linux native support or rotation to a different cookie.

Create/save a Bandcamp download client as the authentication owner, using a distinct, unique
Lidarr Name and an empty Shared Authentication selector. It may be disabled. Explicitly select
that exact name in both indexer and downloader consumers; see [shared authentication](shared-authentication.md).
The helper requires the name; it never guesses an account or writes separate consumer cookies.

Know the intended numeric fan ID independently of this new login. For an existing intended
account, the Bandcamp homepage's `HomepageApp` data-blob contains `pageContext.identity.fanId`.
Inspect that number locally in the intended account's browser; do not export the page, other
cookies, or profile. A username is not a numeric fan ID. Do not infer account identity from a
cookie prefix. For first-time setup, establish the intended account and number before import.

Set `LIDARR_URL` to your Lidarr HTTPS base URL (including any URL base) and supply
`LIDARR_API_KEY` privately through your session/secret manager. Do not put the key into command
arguments, shell history, screenshots, or source files. For example, in PowerShell:

```powershell
$env:LIDARR_URL = 'https://your-lidarr.example/lidarr'
$secret = Read-Host 'Lidarr API key' -AsSecureString
$env:LIDARR_API_KEY = [System.Net.NetworkCredential]::new('', $secret).Password
try {
    dotnet src/Lidarr.Plugin.Bandcamp.CookiePush/bin/Release/net8.0/bandcamp-cookie-push.dll --client-name 'Personal Bandcamp' --fan-id 12345678
} finally {
    Remove-Item Env:LIDARR_API_KEY
    Remove-Variable secret
}
```

Replace the example name, URL and fan ID. HTTPS with normal certificate verification is required;
configure trusted HTTPS or an authenticated TLS tunnel/proxy for a LAN server. HTTP, embedded
URL credentials, query/fragment URLs, and redirect handoffs are refused. The API key grants broad
Lidarr access: keep it only on your trusted desktop, avoid verbose HTTP/Playwright debugging and
screen/transcript recording, and clear the environment variable afterward. Environment variables
and process memory are not encrypted secret storage. The helper does not save the key.

### Authelia-protected Lidarr

If the HTTPS endpoint requires Authelia as well as the Lidarr API key, sign into your
trusted Authelia portal first. Supply **only** its session cookie value privately as
`LIDARR_AUTHELIA_SESSION`, set `LIDARR_AUTHELIA_ORIGIN` to the exact Lidarr HTTPS origin
(for example `https://lidarr.example`, without a URL base), and add `--authelia` to the
helper command. The origin is the protected Lidarr origin, not the Authelia portal's
origin. Nondefault ports must match. `LIDARR_AUTHELIA_COOKIE_NAME` optionally selects a
custom cookie name; the default is `authelia_session`.

Obtain only that secure session cookie from your own authenticated browser or trusted
private tooling. Do not export the profile or a complete cookie header, and do not put
the value in arguments, history, source files or logs. Clear the Authelia environment
variables alongside the API key when finished. This session may authorize other apps
behind your proxy: treat it as a credential. The helper restricts its use to the chosen
Lidarr origin, but does not reduce the credential's authority at the proxy itself.

Proxy authentication is opt-in. Without `--authelia` these variables are ignored; with
it, missing, malformed or mismatched configuration refuses before any HTTP request.
Exactly one cookie is sent to Lidarr, separate from the Bandcamp identity request body.
The Authelia cookie never enters the isolated Bandcamp browser or saved provider settings.
TLS verification remains enabled; redirects remain refused; response cookies are ignored.
Status and cancellation use the same authentication. The helper does not log into
Authelia automatically, refresh its session or bypass its access policy. An expired
proxy session can prevent cancellation acknowledgement: retain the operation ID, sign
in again and reconcile status rather than resubmitting the Bandcamp identity.

On the tested Mac, only .NET 10 was globally installed. The initial explicit
`DOTNET_ROLL_FORWARD=Major` probe provided bounded pre-login evidence only. The later
self-contained Mac ARM64 package instead used its bundled .NET 8 runtime and completed
the [native and HTTPS checks](verification/live-3.2.1.5196/helper-https-2026-11-15/README.md)
without major-version roll-forward. Framework-dependent invocation still expects .NET 8.

## Login and transaction

The helper first verifies that exactly one saved Bandcamp owner matches the name, then opens
visible Chromium at `https://bandcamp.com/login` using a new temporary dedicated profile. Type
credentials, choose the intended account and complete CAPTCHA or emailed verification/2FA
personally. No existing browser profile, password manager database or general cookie export is
read. Only a single secure root-scoped `identity` for `bandcamp.com` crosses the browser boundary.
Top-level navigation stays on exact HTTPS bandcamp.com. Static bcbits resources and explicit
Google/reCAPTCHA resources/frames are permitted for the real login UI; artist sites and unrelated
navigation are blocked. Downloads and service workers are disabled. If Bandcamp changes required
hosts or its login UI, stop and use the manual fallback rather than widening browser access.

The browser closes and the temporary `bandcamp-cookie-push-*` profile is deleted **before**
handoff. The helper sends only the candidate identity in the masked Cookies body field and the
chosen saved owner's id/name, with the expected fan ID as a nonsecret query parameter. It never
prints the identity, its prefix, or its hash. The host action validates the candidate read-only
against the homepage fan-ID contract, checks the intended account and selected owner id (rejecting a reassigned name), and calls the same #49/#50
shared replacement transaction. There is no password login, normal settings PUT, path test,
indexer write, or second cookie store. Validation precedes one detached-settings persistence;
activation follows successful persistence. Search and download then resolve that owner.

Close the login window or press Ctrl-C before handoff to leave settings unchanged. After capture
and profile cleanup, the helper obtains a server-issued operation ID, prints that **nonsecret** ID,
and submits the candidate exactly once. The server binds the ID to the saved owner id and exact
name. Begin/cancel/status carry no identity or other provider secrets. There is no candidate replay.
The synchronous host action still receives no HTTP cancellation token; cancellation uses a separate
host-authenticated action and does not depend on disconnect propagation.

The protocol states are `Pending` → `Validating` → `Committing` → `Committed` (or `Refused`/`Unknown`).
`cancelIdentityRenewal` atomically changes `Pending` or `Validating` to `Cancelled`. A `Cancelled`
receipt acknowledges that **this operation cannot persist**, even if its delayed validation later
finishes or its submission arrives after the cancel. It does not undo another writer's session.
Immediately before entering the existing compare-and-replace store, validation and the operation
share a gate: either cancel wins, or the state becomes `Committing`. This is the explicit point
beyond which cancellation is refused. The actual database write is the durable commit; `Committing`
is an irreversible *attempt*, not proof of success. It includes waiting for storage. No rollback
is attempted. Activation still follows successful persistence through the existing transactional store.

Ctrl-C or the ten-minute CLI deadline stops the local wait, then the helper uses up to **15 extra
seconds** with a fresh token to request cancel and, when needed, read status for the same operation.
A dropped submission response follows the same path. An acknowledged cancel reports unchanged by
this operation; `Committed` reports success even when cancellation lost the race. A still-committing,
unreachable, malformed, or unknown response reports uncertainty, never local timeout as remote cancel.
Forced termination cannot send a cancel. Closing the browser before capture submits nothing; the
browser has already closed before remote validation/persistence begins.

Receipts are process-local, contain no cookie/fingerprint, and use a one-hour lease. New begins
prune expired inactive receipts; active validation/commit receipts remain. Capacity is bounded at
4096 and new begins refuse when full. Only begin can create an ID. Expired, pruned, unknown, or
pre-restart IDs **cannot** submit work, so a delayed cancelled request cannot recreate itself after
restart. Cancel/status are scoped to the original owner id and name; keep administrative renames,
account mapping edits and renewal separate. The transaction coordinates plugin writers in one
process, not arbitrary host/API edits or another Lidarr process.

Profiles are cleaned on success, launch/read failure, window close and cooperative cancellation.
Forced process termination, OS crashes, or filesystem refusal can leave a temporary profile;
close that helper's browser and remove only its dedicated temporary profile after checking it is
no longer active. No secure-erasure or crash-proof cleanup guarantee is made. Do not attach
profiles or raw browser errors to bug reports.

## Status and recovery

Keep the printed operation ID. With the same HTTPS server, API key and exact owner selection,
reconcile without reading stdin, opening a browser or resending the candidate:

```sh
bandcamp-cookie-push --client-name 'Personal Bandcamp' --renewal-status <operation-id>
bandcamp-cookie-push --client-name 'Personal Bandcamp' --cancel-renewal <operation-id>
```

These commands query/cancel that historical operation; a `Committed` receipt does not assert that
its cookie is still current. They never write or restore a cookie. `Cancelled` is definitive for
that operation. Lost cancel acknowledgement remains uncertain to the caller until status confirms
it. A restart, receipt expiry, process crash during persistence, or database write whose success
acknowledgement throws can leave **genuine unresolved commit ambiguity** (`Unknown`). The receipt
is not persisted atomically with settings. Inspect the saved owner privately and current trusted
status; a valid fan ID alone cannot identify which same-account renewal committed. If that does
not establish the outcome, retain UNKNOWN and investigate; do not blindly retry the candidate,
restore a prior cookie over a newer session, or claim cancellation succeeded.

Successful output means read-only validation and persistence both completed for that operation. The helper returns
0 on success, 2 for invalid invocation, and 1 for refusal/error/cancellation. System → Status and
provider Test expose the shared session's typed diagnosis. No secret-bearing server or browser
exception text is echoed.

- `WrongAccount`: the previous identity is retained; log in with the intended account and verify
  the expected numeric fan ID independently.
- `StorageFailure`: persistence acknowledgement failed. The in-memory prior identity is retained,
  but the durable outcome may be UNKNOWN if storage saved before throwing. Inspect storage privately;
  do not assume rollback or overwrite a newer session.
- `ReplacementConflict`: another recovery/settings change won; reread the current owner before retrying.
- `ConfigurationMissing`: use the unique saved owner's exact name; remove chained/self selectors.
- `ChallengeOrUnrecognized`, `CookieExpired`, `Unavailable`, `RateLimited`: follow the corresponding
  Lidarr status; complete login normally or wait/investigate availability, rather than retry passwords.
- No confirmed success: check trusted HTTPS, API key access, server/plugin version and saved status.
  Do not share raw HTTP dumps; they can contain API keys, cookies and existing provider secrets.

## Manual headless fallback

From your own browser's Bandcamp cookie inspector, obtain only `identity` for the intended fan
account. Pass its raw value or `identity=<value>` on stdin to the same helper command for the
same account validation and transaction. Use a private secret-manager pipe or protected stdin;
never put the value in shell arguments, `echo` history, logs or a shared file. The helper refuses
full cookie headers. It supports no `--print-cookie`, `--api-key`, automatic selection, or
`--update-indexer` mode; older #38–#43 instructions for those modes are superseded by #46.

If the helper cannot run, paste only identity into the saved owner's Session Cookie field through
Lidarr and Test it; both shared consumers read that owner. Normal host Save is a manual settings
edit, **outside the validated replacement transaction**, and can overwrite before validation.
Keep the prior value privately until Test succeeds if using that fallback. Clear clipboard and
any privately created input afterward. Local removal does not revoke the Bandcamp server session.

## Reuse and evidence

Selective reuse: #40 command/project scaffold (`a8bdad3`), #41 browser capture/profile lifecycle
(`ce593e8`, merged `037595f`), #42 shared-repair intent (`cc93f29`), and #43 documentation/health
pointers (`df7ce5f`, `dc1be2d`). The older write-before-Test, per-indexer writes and secret-print
paths were deliberately replaced under #46. No whole historical branch merge reverts #48–#50.
The existing credential-login, cache, cooldown, monitor and download retry engine is retained.
Tests exercise HTTP/API and Playwright boundaries with controlled responses and filesystem
cleanup; they do not establish real Bandcamp login, live account parity or supported-platform
runtime qualification. See [initial red/green evidence](verification/issue-51-red-green.txt) and
[cancellation protocol evidence](verification/issue-51-cancellation-red-green.txt).
