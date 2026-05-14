# Lidarr Bandcamp Plugin

[![Build and Release Plugin](https://github.com/jtstothard/lidarr-plugin-bandcamp/actions/workflows/build.yml/badge.svg)](https://github.com/jtstothard/lidarr-plugin-bandcamp/actions/workflows/build.yml)

Native Lidarr plugin that adds **Bandcamp** as both an indexer and a download client. It searches your authenticated Bandcamp collection for owned, downloadable releases, then downloads purchased albums directly into Lidarr.

## Overview

This plugin provides two integrated capabilities:

- **Indexer** — Searches your authenticated Bandcamp collection and returns only releases your Bandcamp account can actually redownload. Results are emitted per real downloadable format so Lidarr can apply its usual quality/profile rules.
- **Download Client** — Downloads the exact format selected from the indexer results and hands it off to Lidarr's import pipeline.

Bandcamp has no official API, so the plugin uses cookie-based authentication to access your account and purchased content. Public catalog search is not used for grabbing because it returns many releases your account cannot actually download.

## Prerequisites

- **Lidarr** nightly branch (required for the plugin system)
- **.NET 8.0** runtime (matching Lidarr's runtime)
- A **Bandcamp account** with purchased music you want to download
- A web browser to export session cookies

## Installation

1. Open Lidarr and navigate to **Settings → Plugins**.
2. Paste the GitHub repository URL into the plugin installer:
   ```
   https://github.com/jtstothard/lidarr-plugin-bandcamp
   ```
3. Click **Install**. Lidarr will download and load the plugin automatically.
4. Restart Lidarr if prompted.

After installation, "Bandcamp" will appear as an available indexer and download client.

## Configuration

### Add the Bandcamp Indexer

1. Go to **Settings → Indexers**.
2. Click **Add Indexer** (`+`).
3. Select **Bandcamp** from the list.
4. Configure:
   - **Session Cookies** — Paste your Bandcamp session cookies (see [Exporting Cookies](#exporting-bandcamp-cookies) below).
   - **Base URL** — Leave as `https://bandcamp.com` unless you have a reason to change it.
5. Click **Test** to verify connectivity, then **Save**.

### Add the Bandcamp Download Client

1. Go to **Settings → Download Clients**.
2. Click **Add Download Client** (`+`).
3. Select **Bandcamp** from the list.
4. Configure:
   - **Session Cookies** — Paste your Bandcamp session cookies (see [Exporting Cookies](#exporting-bandcamp-cookies) below).
   - **Download Path** — Directory where downloads are saved before Lidarr imports them (e.g., `/downloads/bandcamp`). Ensure Lidarr has read/write access.
5. Click **Test**, then **Save**.

### Exporting Bandcamp Cookies

The plugin needs your Bandcamp `identity` cookie to authenticate downloads of purchased albums. Here's how to get it:

#### Chrome / Chromium

1. Log in to [bandcamp.com](https://bandcamp.com).
2. Open **DevTools** (`F12` or `Ctrl+Shift+I` / `Cmd+Option+I`).
3. Go to the **Application** tab → **Cookies** → `https://bandcamp.com`.
4. Find the cookie named `identity`.
5. Copy its **Value** — this is a long string starting with something like `t%3A...`.
6. Paste this value into the **Session Cookies** field in Lidarr.

#### Firefox

1. Log in to [bandcamp.com](https://bandcamp.com).
2. Open **Developer Tools** (`F12` or `Ctrl+Shift+I` / `Cmd+Option+I`).
3. Go to the **Storage** tab → **Cookies** → `https://bandcamp.com`.
4. Find the cookie named `identity`.
5. Copy its **Value**.
6. Paste this value into the **Session Cookies** field in Lidarr.

> **Note:** The `identity` cookie is tied to your browser session. If you log out of Bandcamp or the cookie expires, downloads will fail. Re-export the cookie when this happens.

## Usage

1. **Search** — Use Lidarr's manual or automatic search. The Bandcamp indexer returns owned/downloadable releases from your collection.
2. **Grab** — Lidarr chooses a specific Bandcamp result and format using its normal quality/profile logic.
3. **Download** — The Bandcamp download client fetches the exact selected format into the download path.
4. **Import** — Lidarr imports the downloaded files into your library.

## Troubleshooting

### Plugin doesn't appear in Lidarr

- Ensure you're running Lidarr **nightly** branch (not stable). The plugin system is only available on nightly.
- Check that the repository URL is correct and accessible.
- Restart Lidarr after installing.

### Downloads fail with authentication errors

- Your `identity` cookie has likely expired. Re-export it from your browser and update the settings.
- Make sure you're logged in to Bandcamp in the same browser when exporting the cookie.
- Verify you actually own the album you're trying to download (check your Bandcamp collection at `https://bandcamp.com/yours`).

### Search returns no results

- Check that cookies are configured for the Bandcamp indexer (Settings → Indexers → Bandcamp).
- Verify network connectivity to `bandcamp.com`.
- Search results come only from your owned/downloadable Bandcamp collection. If you have not purchased the release on Bandcamp, it will not appear.

### Build errors when installing from source

- Ensure you have .NET SDK 8.0+ installed.
- Run `dotnet restore` before `dotnet build`.
- See the [Development](#development) section for build instructions.

## Development

### Build from source

```bash
# Restore dependencies (requires cloning Lidarr source for references)
git clone --depth 1 --branch plugins https://github.com/Lidarr/Lidarr.git ext/Lidarr
dotnet restore Lidarr.Plugin.Bandcamp.slnx -p:TreatWarningsAsErrors=false

# Build
dotnet build Lidarr.Plugin.Bandcamp.slnx -c Release -p:TreatWarningsAsErrors=false

# Run tests
dotnet test Lidarr.Plugin.Bandcamp.slnx --no-build -c Release --filter "FullyQualifiedName~Bandcamp" -p:TreatWarningsAsErrors=false
```

The built plugin DLL is at:
```
src/Lidarr.Plugin.Bandcamp/bin/Release/net8.0/Lidarr.Plugin.Bandcamp.dll
```

### CI / Releases

Pushing a tag (`v*`) to the `main` branch triggers the [GitHub Actions workflow](.github/workflows/build.yml), which builds, tests, and publishes a release with the zipped plugin DLL as an asset.

## License

This project is provided as-is under the terms found in the repository. See individual file headers for attribution.
