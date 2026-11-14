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
3. Paste your `identity` cookie into **Session Cookies** (see below for how to get it)
4. **Test**, then **Save**

### Download Client

1. **Settings → Download Clients → Add** (`+`)
2. Pick **Bandcamp**
3. Enter your Bandcamp **Email** and **Password** (recommended), or paste your cookie into **Session Cookie (optional)**. See [Download client sign-in](#download-client-sign-in).
4. Set **Download Path** to somewhere Lidarr can write to (like `/downloads/bandcamp`)
5. **Test**, then **Save**

### Delay Profile (don't skip this)

You need to enable the Bandcamp protocol or Lidarr won't use it for searches:

1. **Settings → Profiles → Delay Profiles**
2. Edit your default profile
3. Toggle **Bandcamp** on
4. Save

## Download client sign-in

The download client has two ways to sign in.

**Email and password (recommended).** The plugin logs in itself, saves the session cookie it gets into **Session Cookie**, and logs in again when that session expires. It checks the session before each grab and once an hour.

**Cookie only.** Leave Email and Password empty and paste your `identity` cookie (see below). This is how earlier versions worked, and existing setups keep working with no changes. You'll need to paste a fresh cookie when it expires.

If both are set, the saved cookie is used while it's valid, and the plugin logs in when it isn't.

When there's no valid session, the plugin refuses new grabs instead of failing them. Lidarr shows a download-client error and backs off, so releases aren't blocklisted or searched again. A health warning on **System → Status** names the cause, and it clears itself once the session works again:

| Health warning cause | What to do |
|---|---|
| Bad credentials | Fix the Email or Password and save. The plugin won't retry a rejected login until you change them. |
| Verification needed | Bandcamp emailed you a code for a new device. Enter it in **Verification Code** and save. It's used once, then cleared. |
| Captcha required | Bandcamp wants a captcha, which the plugin can't solve. Log into bandcamp.com in a browser and paste the `identity` cookie into **Session Cookie**. |
| Cookie expired | Paste a fresh cookie, or add your Email and Password so the plugin renews it for you. |

After a verification or captcha challenge, the plugin waits 6 hours between login attempts so it doesn't hammer Bandcamp or get your account locked. Entering a verification code skips that wait.

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

The cookie expires eventually. With Email and Password set, the plugin renews it for you. Otherwise, grab a fresh one when the health warning says it has expired.

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
- The indexer still uses a pasted `identity` cookie, which expires. Re-export it when searches stop working.
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
