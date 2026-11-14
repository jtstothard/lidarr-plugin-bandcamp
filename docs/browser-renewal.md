# User-assisted shared session renewal (#46 / #51)

Web authentication remains primary. #47's offline decision is **INCONCLUSIVE**; neither live
Subsonic parity nor a credential lifetime is qualified. This helper does not add a login engine,
solve CAPTCHA, collect passwords, disable 2FA, or revive expired cookies by saving them.

## Desktop installation and selection

Run on your own graphical desktop, even when Lidarr runs on a headless server. Install .NET 8
(or newer) and PowerShell 7, clone this repository, then build the standalone helper:

```sh
dotnet build src/Lidarr.Plugin.Bandcamp.CookiePush -c Release
pwsh src/Lidarr.Plugin.Bandcamp.CookiePush/bin/Release/net8.0/playwright.ps1 install chromium
```

Linux also needs Playwright's documented Chromium OS dependencies. No desktop browser belongs
on the headless Lidarr host. The source targets Windows, macOS and Linux; this change has fixture
coverage only, **not native browser/packaging qualification on any platform**. No prebuilt helper
release is claimed. The server must run this plugin version with the `renewIdentity` action.

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

Close the login window or press Ctrl-C to cancel before handoff; the prior saved session remains.
The CLI has a ten-minute total deadline. Once an API request is submitted, cancellation, timeout
or a lost connection cannot prove whether the server committed. The helper reports that ambiguity:
inspect Lidarr's saved owner/status before retrying, rather than treating timeout as rollback.
The synchronous host action has no request-cancellation propagation; its persistence is the commit
point. The transaction coordinates plugin writers within one process, not arbitrary host/API edits
or another Lidarr process. Keep administrative account mapping edits separate from renewal.

Profiles are cleaned on success, launch/read failure, window close and cooperative cancellation.
Forced process termination, OS crashes, or filesystem refusal can leave a temporary profile;
close that helper's browser and remove only its dedicated temporary profile after checking it is
no longer active. No secure-erasure or crash-proof cleanup guarantee is made. Do not attach
profiles or raw browser errors to bug reports.

## Status and recovery

Successful output means read-only validation and persistence both completed. The helper returns
0 on success, 2 for invalid invocation, and 1 for refusal/error/cancellation. System → Status and
provider Test expose the shared session's typed diagnosis. No secret-bearing server or browser
exception text is echoed.

- `WrongAccount`: the previous identity is retained; log in with the intended account and verify
  the expected numeric fan ID independently.
- `StorageFailure`: the previous identity is retained; repair Lidarr database storage/permissions.
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
runtime qualification. See [red/green evidence](verification/issue-51-red-green.txt).
