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
3. Paste your `identity` cookie into **Session Cookie (optional)**. You can also add your Bandcamp **Email** and **Password**, but Bandcamp currently asks for a captcha on every login. See [Download client sign-in](#download-client-sign-in).
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

**Email and password.** The plugin logs in itself, saves the session cookie it gets into **Session Cookie**, and logs in again when that session expires. It checks the session before each grab and once an hour.

**Cookie only.** Leave Email and Password empty and paste your `identity` cookie (see below). This is how earlier versions worked, and existing setups keep working with no changes. Use the [cookie refresh helper](#cookie-refresh-helper) or paste a fresh cookie when it expires.

If both are set, the saved cookie is used while it's valid, and the plugin logs in when it isn't.

When there's no valid session, the plugin refuses new grabs instead of failing them. Lidarr shows a download-client error and backs off, so releases aren't blocklisted or searched again. A health warning on **System → Status** names the cause, and it clears itself once the session works again:

| Health warning cause | What to do |
|---|---|
| Bad credentials | Fix the Email or Password and save. The plugin won't retry a rejected login until you change them. |
| Verification needed | Bandcamp emailed you a code for a new device. Enter it in **Verification Code** and save. It's used once, then cleared. |
| Captcha required | Bandcamp wants a captcha (as of November 2026 it asks for one on every login, so expect this if you use email and password), which the plugin can't solve. Use the [cookie refresh helper](#cookie-refresh-helper), or log into bandcamp.com in a browser and paste the `identity` cookie into **Session Cookie**. |
| Cookie expired | Use the [cookie refresh helper](#cookie-refresh-helper), or paste a fresh cookie. Email/password renewal cannot get past a captcha. |

After a verification or captcha challenge, the plugin waits 6 hours between login attempts so it doesn't hammer Bandcamp or get your account locked. Entering a verification code skips that wait.

Email, password, verification code and cookie are stored in Lidarr's settings as masked fields and never logged. Anyone with access to Lidarr's database or API key can read them.

## Cookie refresh helper

`bandcamp-cookie-push` opens a visible Chromium browser so you can log in yourself, then sends the captured `identity` cookie to an existing Bandcamp download client through Lidarr's API and runs its **Test**. Run it on a Windows, macOS or Linux desktop with access to your Lidarr server; it does not need to run on the server. Add the Bandcamp download client in Lidarr first.

### Install from source

You need Git, the .NET 8 SDK (or a newer SDK plus the .NET 8 runtime), and PowerShell (`pwsh`, including on macOS/Linux) for Playwright's browser installer. From a terminal:

```sh
git clone https://github.com/jtstothard/lidarr-plugin-bandcamp.git
cd lidarr-plugin-bandcamp
dotnet build src/Lidarr.Plugin.Bandcamp.CookiePush -c Release
pwsh src/Lidarr.Plugin.Bandcamp.CookiePush/bin/Release/net8.0/playwright.ps1 install chromium
```

On Linux, Chromium may also need system packages: use the same installer with `install --with-deps chromium` (this may require administrator access). This builds the standalone helper; cloning Lidarr's source and installing the plugin on this desktop are unnecessary. Run commands below from the repository directory. You can check the available options without opening a browser:

```sh
dotnet src/Lidarr.Plugin.Bandcamp.CookiePush/bin/Release/net8.0/bandcamp-cookie-push.dll --help
```

### Connect and log in

Set `LIDARR_URL` to your Lidarr base URL, including any URL base such as `/lidarr`, and `LIDARR_API_KEY` to the key from **Settings → General → Security**. For example, in bash:

```sh
export LIDARR_URL='http://localhost:8686'
read -r -s -p 'Lidarr API key: ' LIDARR_API_KEY  # bash
export LIDARR_API_KEY
```

In zsh, use `read -r -s 'LIDARR_API_KEY?Lidarr API key: '` instead of the bash `read` line. In PowerShell:

```powershell
$env:LIDARR_URL = 'http://localhost:8686'
$env:LIDARR_API_KEY = Read-Host 'Lidarr API key' -MaskInput  # PowerShell 7.1+
```

Enter the key at the prompt so it is not saved in shell history. Then run:

```sh
dotnet src/Lidarr.Plugin.Bandcamp.CookiePush/bin/Release/net8.0/bandcamp-cookie-push.dll
```

1. A fresh temporary browser profile opens at `https://bandcamp.com/login`. It does not reuse your normal browser's signed-in session, extensions or password manager profile.
2. Type your Bandcamp credentials and complete any captcha or emailed verification code in that window. The helper does not take credentials as inputs or solve challenges.
3. Once Bandcamp sets an `identity` cookie, the helper captures it, closes the browser and deletes the temporary profile on normal completion or handled failure. Closing the window before capture reports an error without changing Lidarr. A forced termination may leave the temporary profile behind.
4. The helper selects the sole Bandcamp download client, saves its Session Cookie and runs Lidarr's download-client **Test**. It reports success or an error without printing the cookie or API key by default. A failed Test does not undo the saved cookie.

### Options

Flags override environment variables. Only the URL and API key have environment-variable equivalents.

| Flag | Environment / behavior |
|---|---|
| `--url <url>` | `LIDARR_URL`; absolute HTTP or HTTPS base URL. Use HTTPS when crossing an untrusted network. |
| `--api-key <key>` | `LIDARR_API_KEY`; prefer the environment variable to avoid putting the key in command history or process arguments. |
| `--client-id <id>` | Select an existing Bandcamp download client by positive numeric id. |
| `--client-name <name>` | Select by name, case-insensitively; quote names containing spaces. Use either id or name, not both. With neither, exactly one Bandcamp client must exist. |
| `--update-indexer` | Select both providers before writing, then save and test the download client followed by the Bandcamp indexer. |
| `--indexer-id <id>` | Select the indexer by positive numeric id; requires `--update-indexer` to have an effect. |
| `--indexer-name <name>` | Select the indexer by name, case-insensitively. Use either indexer id or name, not both. With neither, exactly one Bandcamp indexer must exist. |
| `--print-cookie` | Capture and print `identity=<cookie>` without contacting Lidarr; no URL or API key needed. Takes precedence over dry-run and indexer updates. |
| `--dry-run` | Capture a cookie and read Lidarr's download-client list to report the selected client. With `--update-indexer`, also read, select and report the indexer. Requires URL/API key; sends no PUT or POST and runs no provider Test. Missing or ambiguous selections fail before any writes. |
| `-h`, `--help` | Show usage; use alone. |

For example, to refresh both configured providers:

```sh
dotnet src/Lidarr.Plugin.Bandcamp.CookiePush/bin/Release/net8.0/bandcamp-cookie-push.dll --client-name 'Bandcamp downloads' --update-indexer --indexer-name 'Bandcamp collection'
```

Both requested providers are selected before any writes. The download-client update and Test then happen first. A later indexer save or Test failure leaves that client updated; there is no rollback. Check the reported step before retrying.

To log in and paste the cookie manually, use `--print-cookie`. This deliberately exposes the session secret in terminal output; browser capture also writes progress lines, so stdout is not a cookie-only stream. Treat the cookie like a password and avoid sharing or logging that output. Paste the `identity=...` line into the appropriate Lidarr cookie field.

To check provider selection without writing settings, use `--dry-run` (add `--update-indexer` to check both providers). It still opens the login browser unless you supply a cookie on stdin. Redirected stdin accepts a bare cookie value or `identity=...`; a nonempty value skips the browser. Never pass a cookie as a command-line argument. If using a file, protect and delete it yourself: the helper does not manage that file. Empty stdin falls back to browser capture.

Press Ctrl+C to cancel. The helper cancels pending HTTP requests, closes the capture browser and removes its temporary profile after pending browser operations settle, then exits with code 130 and a short message without secrets or a stack trace. Playwright browser launch has no cancellation-token API: cancellation during launch waits for launch to settle (30-second launch timeout) before closing any resulting context and deleting the profile. Cancellation does not undo completed Lidarr writes; check settings before retrying. Normal updates also resolve both requested providers before the first write, but a later Test failure or cancellation can leave an earlier update saved.

If Lidarr cannot be reached, check the URL, URL base and network connection. A rejected API key needs correcting in `LIDARR_API_KEY`. If no client is found, add a Bandcamp download client; if selection is ambiguous, use an id or unique name. If the login window closes early, rerun and finish login before closing it.

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

The cookie expires eventually. Use the [cookie refresh helper](#cookie-refresh-helper) or copy a fresh one when the health warning says it has expired. Email/password renewal only works when Bandcamp does not demand a captcha.

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
- The indexer uses an `identity` cookie, which expires. Refresh it manually or with the helper’s `--update-indexer` option when searches stop working.
- The plugin's credential login cannot solve a Bandcamp captcha. Use the helper's browser flow or paste a cookie instead.
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
