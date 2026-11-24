# Lidarr Bandcamp Plugin

[![Build](https://github.com/jtstothard/lidarr-plugin-bandcamp/actions/workflows/build.yml/badge.svg)](https://github.com/jtstothard/lidarr-plugin-bandcamp/actions/workflows/build.yml)

Bandcamp indexer and download client for Lidarr. Searches your Bandcamp collection and downloads albums you've bought.

> **This only works with music you own.** It connects to your Bandcamp account and searches your purchase history. If you haven't bought it on Bandcamp, it won't show up.

## Requirements

- Lidarr on the **nightly** branch (plugins aren't on stable yet)
- A Bandcamp account with at least one purchased album
- Your Bandcamp email and password, or a browser to grab your session cookie

## Installing

1. In Lidarr, go to **System → Plugins**
2. Paste `https://github.com/jtstothard/lidarr-plugin-bandcamp`
3. Click **Install**, restart if asked

Bandcamp will now show up as an available indexer and download client.

## Setup

### Indexer

1. **Settings → Indexers → Add** (`+`)
2. Pick **Bandcamp** from the list
3. Set **Shared Authentication** to the exact name of your saved Bandcamp download client (configure that first), or leave it empty and paste an independent `identity` cookie into **Session Cookies**.
4. **Test**, then **Save**

### Download Client

1. **Settings → Download Clients → Add** (`+`)
2. Pick **Bandcamp**
3. Paste your `identity` cookie into **Session Cookie (optional)**. You can also add your Bandcamp **Email** and **Password**, but a recorded login required CAPTCHA; unattended recovery is not guaranteed. See [Download client sign-in](#download-client-sign-in).
4. Set **Download Path** to somewhere Lidarr can write to (like `/downloads/bandcamp`)
5. **Test**, then **Save**

### Shared authentication for search and download

A saved Bandcamp download client is a named authentication owner when its **Shared Authentication**
field is empty. Give it a unique name, for example `Bandcamp personal`. Set the indexer's
**Shared Authentication** to that exact name. Additional download clients can select the same
owner. Renew or paste the identity only on the owner; search and downloads use its current saved
session. A disabled owner can still supply authentication without becoming an enabled downloader.

Upgrades retain every legacy cookie, credential pair, enablement flag and provider option.
Nothing guesses which cookies belong to the same account. Leave selectors empty to preserve
independent legacy behavior, or explicitly map each consumer to the correct account's owner.
Local consumer cookies/credentials remain saved but are ignored while a shared owner is selected;
they are not fallback identities. Missing, duplicate or chained owner names cause an actionable
refusal. Rename consumers' selectors when renaming an owner. See [migration and downgrade
limitations](docs/shared-authentication.md) before switching accounts or downgrading.

### Delay Profile (don't skip this)

You need to enable the Bandcamp protocol or Lidarr won't use it for searches:

1. **Settings → Profiles → Delay Profiles**
2. Edit your default profile
3. Toggle **Bandcamp** on
4. Save

## Download client sign-in

The download client has two ways to sign in.

**Email and password.** The plugin can attempt Bandcamp web login when a session is missing or positively signed out, and saves a successful session into **Session Cookie**. CAPTCHA or verification can require your help. It checks the session before each grab and once an hour.

**Cookie only.** Leave Email and Password empty and paste your `identity` cookie (see below). This is how earlier versions worked, and existing setups keep working with no changes. You'll need to paste a fresh cookie when it expires.

If both are set, the saved cookie is preferred. A challenge, unrecognized response, rate limit or outage keeps that identity and does not trigger password login.

When there's no valid session, the plugin refuses new grabs instead of failing them. Lidarr shows a download-client error and backs off, so releases aren't blocklisted or searched again. A health warning on **System → Status** names the cause, and it clears itself once the session works again:

| Health warning cause | What to do |
|---|---|
| Bad credentials | Fix the Email or Password and save. The plugin won't retry a rejected login until you change them. |
| Verification needed | Bandcamp emailed you a code for a new device. Enter it in **Verification Code** and save. It's used once, then cleared. |
| Captcha required | Bandcamp wants a captcha, which the plugin can't solve. A recorded November 2026 login required one; unattended password recovery cannot be promised. Run the [desktop renewal helper](#browser-or-desktop-renewal-helper), or log into bandcamp.com in a browser and paste the `identity` cookie into **Session Cookie**. |
| Signed out | Run the [desktop renewal helper](#browser-or-desktop-renewal-helper), paste a fresh cookie, or configure complete Email and Password credentials for a login attempt. |
| No session configured | Enter complete Email and Password credentials or paste a browser identity cookie. |
| Challenge or unrecognized response | Check Bandcamp in your browser; renew manually if needed. The saved identity is retained. |
| Rate limited | Wait until the displayed UTC retry time. Do not change credentials. |
| Temporary network/server failure | Check network and Bandcamp availability and try later. The saved identity is retained. |

Warnings also show the effective web auth mode and the last successful validation time. [Validation and retry policy](docs/session-validation-status.md) describes the response evidence and fallback wait.

After a verification or captcha challenge, the plugin waits 6 hours between login attempts so it doesn't hammer Bandcamp or get your account locked. Entering a verification code skips a verification wait, but cannot bypass a CAPTCHA wait.

Email, password, verification code and cookie are stored in Lidarr's settings as masked fields and never logged. Anyone with access to Lidarr's database or API key can read them.

## Getting your cookie

The plugin authenticates using the `identity` cookie from your browser.

**Chrome:**
1. Log into [bandcamp.com](https://bandcamp.com)
2. Open DevTools (`F12`) → **Application** tab → **Cookies** → `https://bandcamp.com`
3. Find `identity`, copy its value
4. Paste that into the indexer's **Session Cookies** field, or the download client's **Session Cookie (optional)** field

**Firefox:**
1. Log into [bandcamp.com](https://bandcamp.com)
2. Open Developer Tools (`F12`) → **Storage** tab → **Cookies** → `https://bandcamp.com`
3. Find `identity`, copy its value
4. Paste that into the indexer's **Session Cookies** field, or the download client's **Session Cookie (optional)** field

No fixed cookie lifetime is assumed. If Bandcamp positively signs the session out, the plugin can attempt credential login; CAPTCHA or verification may require browser/manual recovery. With shared authentication selected, update the owner only. Legacy independent configurations still require separate cookie updates.

## Supported formats

| Format | Type |
|--------|------|
| FLAC | Lossless |
| ALAC | Lossless |
| WAV | Lossless (uncompressed) |
| AIFF | Lossless (uncompressed) |
| MP3 V0 | Lossy |
| MP3 320 | Lossy |
| OGG Vorbis | Lossy |
| AAC | Lossy |

Lidarr picks the best one based on your quality profile.

## Limitations

- Only downloads albums you've bought on Bandcamp. No public search.
- Shared search uses the selected owner's current session. Legacy independent search still needs its own pasted identity.
- Login can't get past a Bandcamp captcha. Paste a cookie instead.
- Searches your collection, not the full Bandcamp catalog.

## Troubleshooting

**Plugin doesn't show up in Lidarr**
You need the nightly branch, not stable. Go to Settings → General → change branch to `nightly`, save, and update.

**Authentication errors / grabs refused**
Check the Bandcamp health warning on System → Status and follow the matching step in [Download client sign-in](#download-client-sign-in).

**Search returns nothing**
Check that you actually bought the album on Bandcamp. Also make sure the Bandcamp protocol is enabled in your Delay Profile (Settings → Profiles → Delay Profiles).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for build instructions and PR guidelines.

## License

MIT. See [LICENSE](LICENSE).

## Support the project

[☕ Ko-fi](https://ko-fi.com/trshpotato)

## Browser or desktop renewal helper

For CAPTCHA, verification/2FA, and headless servers, use the [isolated desktop helper and manual fallback](docs/browser-renewal.md). It validates the intended fan account and transactionally replaces one named shared owner for both search and downloads. Native browser/platform qualification remains pending.

Local credential clearing and known host DB/API/backup/Trace exposure are described in the
[secret-hygiene guide](docs/secret-hygiene.md). The [bounded web-canary protocol](docs/web-canary.md)
is opt-in only; its default tool invocation performs no account access.

## Lidarr runtime compatibility

The corrected session-health check supports the pinned Lidarr 3.1.4.5029 and
3.2.1.5196 nightly builds. See the [support policy and packaged-runtime checks](tests/PackagedCompatibility/SUPPORT.md)
for exact inputs, scope and reproduction commands. Other builds require qualification.
