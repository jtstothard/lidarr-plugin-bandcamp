# Web session validation and recovery (#48)

The session provider uses the homepage fan-ID contract from ADR 0001. A positive numeric fan ID on HTTP 200 proves a live fan session. A recognized identity with `fanId: null` and `isLoggedIn: false` proves sign-out on HTTP 200, 401 or 403. A bare 401/403, malformed JSON, empty page, redesigned page or challenge does not prove sign-out. Other responses default conservatively to challenge/unrecognized. HTTP 429 takes priority over body evidence; HTTP 5xx and transport failures mean temporary unavailability.

Only a missing or positively signed-out identity permits the existing password-login engine. The decision is local to each validation attempt, so concurrent status updates cannot grant permission to log in. A valid cookie remains preferred. Successful checks are cached for 15 minutes; a forced inconclusive or availability check does not shorten that successful cache. The hourly monitor makes one validation/recovery call per due definition. The pre-grab gate refuses a grab with its actual cause; the existing single mid-download renewal/retry contract is unchanged.

## Retry policy

Homepage validation and both login endpoints recognize HTTP 429. An unsigned delta in seconds or HTTP date (IMF-fixdate, RFC 850 or asctime) supplies the retry deadline. Missing, duplicate, malformed, negative, fractional or unrepresentably large values use a five-minute fallback. Zero and past dates wait at least one second, preventing tight retry loops. Representable large deltas saturate at the latest representable UTC date rather than overflowing. No validation or password attempt runs during the wait. Afterward the next session request validates again, even if an earlier successful result would otherwise remain cached. This wait is process-local; no durable rate-limit guarantee is made.

## Status and actions

`IBandcampSessionProvider.State` exposes the session status, problem code/action, effective web auth mode and last successful validation UTC time. Failure warnings include the mode and time (or `never`). Healthy sessions clear health warnings. The last successful time is process-local and changes only after a successful live validation; it is not a cookie expiry prediction. There is no separate status dashboard; shared consumers expose the owner's typed state and identify shared configuration as their effective mode.

- Missing or positively signed out: configure complete credentials or renew in a browser and paste the identity.
- Rejected credentials: correct email/password; suppression until they change remains in force.
- Verification required: enter the emailed code; existing single-use handling remains in force.
- CAPTCHA required: complete browser login and paste an identity; a verification code cannot bypass CAPTCHA cooldown.
- Challenge/unrecognized: inspect Bandcamp in a browser; renew manually only if needed. Keep the saved identity.
- Rate limited: wait until the displayed UTC deadline; do not change credentials.
- Temporary network/server failure: investigate connectivity/service availability and try later. Keep the saved identity.

Durable challenge suppression and transactional replacement are implemented by #49. #50 adds [explicit named sharing](shared-authentication.md) through that same provider and store. Unmapped legacy indexers remain independent.

## Evidence limits

Tests use the pre-existing valid/signed-out homepage and login response fixtures at the HTTP boundary. New challenge, redesign, empty and malformed homepage fixtures are deliberately synthetic counterexamples, not live captures. Their file-based replay verifies conservative handling, not current Bandcamp markup or production compatibility. No live account actions, deployment or PR were performed. See [red/green evidence](verification/issue-48-red-green.txt).
