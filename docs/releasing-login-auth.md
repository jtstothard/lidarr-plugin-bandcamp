# Releasing login-based auth (#26)

## Version

This is a minor release: tag `v1.1.0` (previous release `v1.0.9`). CI takes the version from the tag (`-p:Version=${GITHUB_REF_NAME#v}`), so tagging is the version bump.

## Before re-enabling the production download client

The production client was disabled to stop the grab/fail loop. Re-enable it only after both of these pass:

1. **ConsoleHarness smoke test against a real account.** Run `tests/Lidarr.Plugin.Bandcamp.ConsoleHarness` with the account's email and password and no cookie. Confirm the login gets an identity cookie and the fan_id resolves. The login endpoint shape (`/login` crumb then `/login_cb` POST) was built from memory, not captured live, so this is the first real check. If Bandcamp challenges the login, confirm the verification-code or captcha path behaves as documented in the README.
2. **One gated purchase grab.** Install the release on production with the client still disabled, enter credentials, press **Test**, then enable the client and grab one purchased album. Confirm it completes and imports, and that the Bandcamp health check is clear.

If either fails, leave the client disabled. Cookie-only mode with the pre-grab gate still stops the loop.
