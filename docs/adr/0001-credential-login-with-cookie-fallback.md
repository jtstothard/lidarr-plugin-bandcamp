# 1. Credential login with cookie fallback; session written back to settings

- Status: accepted
- Date: 2026-11-14
- Issues: #26 (spec), #31 (credential login)

## Context

The download client authenticated only with a pasted `identity` cookie. Bandcamp offers no
OAuth for fan accounts. When the cookie expired nothing renewed it, every grab failed at fan_id
resolution, and Lidarr blocklisted and re-searched in a loop. Recovering meant digging a new
cookie out of browser DevTools.

## Decision

- The client settings gain `Email` and `Password` (`PrivacyLevel.Password`). `Cookies` keeps its
  name and becomes the optional fallback, so saved configs, cookie-only installs and downgrades
  keep working with no migration. Validation needs email and password, or a cookie.
- The session provider owns login. `GetSessionAsync` and `RenewAsync` first validate the stored
  cookie with the live fan_id lookup. A login runs only when that cookie is dead or missing and
  credentials are set, so a valid cookie is always preferred and non-auth failures never cause a
  login.
- Concurrent callers share one login: the provider keeps a single in-flight login task, and a
  caller that saw a cookie fail after another caller already replaced it re-validates instead.
- After a successful login the identity cookie is written back: first onto the in-memory
  settings object (so every holder of the definition sees it immediately), then into the persisted
  definition through `IDownloadClientRepository.SetFields(definition, d => d.Settings)`. A restart
  therefore doesn't force a new login, and an older plugin version still finds a working cookie.
- The login client uses Bandcamp's web login form: `GET https://bandcamp.com/login` for pre-login
  cookies and the `login_cb` crumb, then a form `POST https://bandcamp.com/login_cb` with
  `user.name`, `login.password`, `login.twofactor`, `login.twofactor_remember`, `login.from` and
  `login.crumb`. The JSON reply (`{"ok":true}` or `{"ok":false,"errors":[{"field":...}]}`) maps to
  a typed outcome: Success, BadCredentials, VerificationRequired, CaptchaRequired, Error. The
  identity cookie comes from the `Set-Cookie` header. All requests go through
  `BandcampHttpClient` (rate limiting, credential-safe logging).
- Bad credentials are never retried automatically: the provider remembers a hash of the rejected
  email and password and refuses to log in again until either changes.
- Credentials live in Lidarr's provider settings (its database). The threat model is Lidarr's
  own: anyone with DB or API-key access can read them.

## Consequences

- Users can enter email and password instead of a cookie; renewals need no user action.
- Bandcamp's login form is undocumented and protected by challenges. The field names and response
  shapes come from observing the web login and are pinned by recorded fixtures under
  `tests/.../LoginFixtureData`; if Bandcamp changes the form the outcome becomes `Error`, and a
  pasted cookie still works. Verification codes and captchas (#32) fall back to cookie paste.
- The mobile app's `/oauth_login` flow (proof-of-work and HMAC headers) was rejected as harder to
  maintain than the web form.

## Update 2026-11-14

A live check showed Bandcamp currently requires a reCAPTCHA token on every web login (`login.captcha` / `missing`). Credential login therefore ends in Captcha required today, and the pasted cookie is the working path. The decision stands: the flow is correct if Bandcamp stops demanding a captcha for some logins, and the gate, health check and write-back still apply to cookie mode.

## Update: transactional recovery (#46, #49)

The original in-memory-first write-back order above is superseded. Login-issued and externally
obtained candidate identities now use the session provider's `ReplaceIdentityAsync` transaction:
read-only fan validation, comparison with the current identity/credentials, one saved-settings
write on a detached copy, then activation in memory. Invalid candidates, cancellation before the
commit point, storage errors and superseded recoveries retain the previous identity. Storage
failures have a separate health problem; they are not reported as rejected Bandcamp credentials.
The store serializes plugin writes per definition and compares the saved identity/credentials
before replacement. This coordinates plugin recovery writers in one Lidarr process; it does not
provide a database compare-and-swap against unrelated host/API writers or another process.

Challenge cause and UTC deadline are persisted as settings metadata, preserving the legacy
`Cookies` representation and defaulting to no challenge for older settings. Restarted providers
honor the remaining cooldown; verification codes answer verification challenges only, never
CAPTCHA. If saving challenge metadata fails, the current process still suppresses retries and
reports storage failure, but restart durability cannot be guaranteed until storage works.
Partial email/password pairs are rejected even when a cookie is configured.

Regression evidence uses the approved settings, session/credential-login, monitor/health and
public download seams with controlled time, serialized persisted rereads, cancellation,
competing recovery providers and injected repository failures. No live account qualification
is implied by those fixtures.

## Update: named shared ownership (#46, #50)

A saved Bandcamp downloader with an empty Shared Authentication selector is a named auth owner.
Indexer and downloader definitions may explicitly select its unique Lidarr Name. The registry
resolves saved settings for execution, keys the existing engine by owner id, and delegates to
that engine. It does not add credential-login scheduling or silently infer account equivalence.
Legacy definitions stay independent until deliberately mapped, with every legacy secret retained.

This uses the existing host provider settings/repository/API instead of a second persistence
system. One validated persisted replacement updates the owner alone and repairs both consumers.
The supported downgrade representation is the owner's legacy cookie/credential fields; consumer
legacy fields are retained but not synchronized. Before downgrade they require deliberate account
reconciliation and cookie copy. See [migration and limitations](../shared-authentication.md).
