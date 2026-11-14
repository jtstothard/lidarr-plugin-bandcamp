# Glossary

**Fan session**
: An authenticated Bandcamp fan account session. It is usable when the live fan_id lookup on the
  Bandcamp homepage returns a fan id. That lookup is the only test of liveness.

**Identity cookie**
: The `identity` cookie Bandcamp issues to a logged-in fan. It is the fan session's token, stored in
  the client's `Cookies` setting, either pasted by the user or written there after a login. Its own
  expiry field is ignored.

**Session provider**
: The per-client-definition component that hands out the current fan session. It validates the
  identity cookie, logs in with the stored credentials when the cookie is dead (one login at a time),
  writes the fresh cookie back to settings, and records the session state.

**Login challenge**
: A login Bandcamp won't complete with email and password alone: an emailed verification code
  (VerificationRequired) or a captcha (CaptchaRequired). Captchas fall back to cookie paste.

**Pre-grab auth gate**
: The check in `Download()` that asks the session provider for a valid session before enqueueing a
  grab. With no session available, the grab fails as a download-client failure (Lidarr backoff)
  instead of a failed, blocklisted release.
