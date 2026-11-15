# Glossary

**Fan session**
: An authenticated Bandcamp fan account session. It is usable when the live fan_id lookup on the
  Bandcamp homepage returns a fan id. That lookup is the only test of liveness.

**Identity cookie**
: The `identity` cookie Bandcamp issues to a logged-in fan. It is the fan session's token, stored in
  the authentication owner's `Cookies` setting, either pasted by the user or written there after a login. Its own
  expiry field is ignored.

**Authentication owner / named configuration**
: A saved Bandcamp download-client definition with a unique Name and empty Shared Authentication
  selector. It owns one account's cookie, credentials and challenge state in existing Lidarr provider
  settings. Consumers select its exact name explicitly; no account equivalence is inferred.

**Shared Authentication selector**
: The name of an authentication owner selected by an indexer or downloader. Local legacy secrets
  remain saved but inactive while selected. Empty means independent legacy authentication.

**Session provider**
: The per-authentication-owner component that hands out the current fan session. It validates the
  identity cookie, logs in with the stored credentials when the cookie is dead (one login at a time),
  validates a replacement before atomically saving it, and records the typed session state.
  Named consumers resolve current saved owner settings when executing; they do not own cookie copies.

**Login challenge**
: A login Bandcamp won't complete with email and password alone: an emailed verification code
  (VerificationRequired) or a captcha (CaptchaRequired). Captchas fall back to cookie paste.

**Pre-grab auth gate**
: The check in `Download()` that asks the session provider for a valid session before enqueueing a
  grab. With no session available, the grab fails as a download-client failure (Lidarr backoff)
  instead of a failed, blocklisted release.

**User-assisted renewal**
: A visible isolated Bandcamp login on the user's desktop, followed by minimal identity extraction and read-only expected-fan validation through the named owner's existing replacement transaction. The user personally completes CAPTCHA, verification and account selection.
