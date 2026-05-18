# Lidarr Bandcamp Plugin

[![Build](https://github.com/jtstothard/lidarr-plugin-bandcamp/actions/workflows/build.yml/badge.svg)](https://github.com/jtstothard/lidarr-plugin-bandcamp/actions/workflows/build.yml)

Bandcamp indexer and download client for Lidarr. Searches your Bandcamp collection and downloads albums you've bought.

> **This only works with music you own.** It connects to your Bandcamp account and searches your purchase history. If you haven't bought it on Bandcamp, it won't show up.

## Requirements

- Lidarr on the **nightly** branch (plugins aren't on stable yet)
- A Bandcamp account with at least one purchased album
- A browser to grab your session cookie

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
3. Same cookie as above in **Session Cookies**
4. Set **Download Path** to somewhere Lidarr can write to (like `/downloads/bandcamp`)
5. **Test**, then **Save**

### Delay Profile (don't skip this)

You need to enable the Bandcamp protocol or Lidarr won't use it for searches:

1. **Settings → Profiles → Delay Profiles**
2. Edit your default profile
3. Toggle **Bandcamp** on
4. Save

## Getting your cookie

The plugin authenticates using the `identity` cookie from your browser.

**Chrome:**
1. Log into [bandcamp.com](https://bandcamp.com)
2. Open DevTools (`F12`) → **Application** tab → **Cookies** → `https://bandcamp.com`
3. Find `identity`, copy its value
4. Paste that into the Session Cookies field

**Firefox:**
1. Log into [bandcamp.com](https://bandcamp.com)
2. Open Developer Tools (`F12`) → **Storage** tab → **Cookies** → `https://bandcamp.com`
3. Find `identity`, copy its value
4. Paste that into the Session Cookies field

The cookie expires eventually. When downloads start failing, just grab a fresh one.

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
- The `identity` cookie expires. Re-export it when things stop working.
- Searches your collection, not the full Bandcamp catalog.

## Troubleshooting

**Plugin doesn't show up in Lidarr**
You need the nightly branch, not stable. Go to Settings → General → change branch to `nightly`, save, and update.

**Authentication errors / downloads fail**
Your cookie expired. Get a fresh one from your browser.

**Search returns nothing**
Check that you actually bought the album on Bandcamp. Also make sure the Bandcamp protocol is enabled in your Delay Profile (Settings → Profiles → Delay Profiles).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for build instructions and PR guidelines.

## License

MIT. See [LICENSE](LICENSE).

## Support the project

[☕ Ko-fi](https://ko-fi.com/trshpotato)
