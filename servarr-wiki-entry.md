# Servarr Wiki Entry — Bandcamp Plugin

This is a proposed addition to the [Lidarr Plugins](https://wiki.servarr.com/en/lidarr/plugins) page, formatted to match the existing plugin entries. It would be added as a new section after the existing plugin entries.

---

## jtstothard/lidarr-plugin-bandcamp

[Bandcamp by jtstothard](https://github.com/jtstothard/lidarr-plugin-bandcamp) is a Lidarr plugin for Bandcamp integration:

* **Indexer & Download Client**: Searches your authenticated Bandcamp collection and downloads purchased albums directly
* **Cookie Authentication**: Uses the Bandcamp `identity` cookie for secure account access
* **Multiple Formats**: Supports FLAC, ALAC, WAV, AIFF, MP3 V0, MP3 320, OGG Vorbis, and AAC

### Prerequisites

* A working Lidarr installation on the `nightly` branch (see [Lidarr Plugins Branch](https://wiki.servarr.com/en/lidarr/plugins#lidarr-plugins-branch))
* A **Bandcamp account** with purchased music — this plugin can only download music you own
* The `identity` cookie from your browser session on bandcamp.com

> **Owned Music Only**: This plugin searches and downloads from your Bandcamp collection. It cannot download music you have not purchased.

### Post-Install Configuration

#### Indexer

* Navigate to `/settings/indexers`, and select the **+** button under Indexers. Bandcamp will appear under the Other section.
* Paste your Bandcamp `identity` cookie value into the **Session Cookies** field.
* Leave the **Base URL** as `https://bandcamp.com`.
* Select the **Test** button.
* If the Test returns a green checkmark, select **Save**.

#### Download Client

* Navigate to `/settings/downloadclients`, and select the **+** button under Download clients. Bandcamp will appear under the Other section.
* Paste your Bandcamp `identity` cookie value into the **Session Cookies** field.
* Set the **Download Path** to a directory Lidarr can read and write (e.g., `/downloads/bandcamp`).
* Select the **Test** button.
* If the Test returns a green checkmark, select **Save**.

#### Delay Profile

* Navigate to `/settings/profiles` and scroll down to Delay Profiles.
* Select the wrench icon on the right side of the profile you wish to use Bandcamp with. Most installations will only have a Default profile.
* Enable the **Bandcamp** protocol, and select **Save**.

### Exporting Cookies

The plugin requires your Bandcamp `identity` cookie to authenticate.

**Chrome / Chromium:**

1. Log in to [bandcamp.com](https://bandcamp.com).
2. Open DevTools (`F12` or `Ctrl+Shift+I` / `Cmd+Option+I`).
3. Go to the **Application** tab → **Cookies** → `https://bandcamp.com`.
4. Find the cookie named `identity`.
5. Copy its **Value** and paste it into the Session Cookies field in Lidarr.

**Firefox:**

1. Log in to [bandcamp.com](https://bandcamp.com).
2. Open Developer Tools (`F12`).
3. Go to the **Storage** tab → **Cookies** → `https://bandcamp.com`.
4. Find the cookie named `identity`.
5. Copy its **Value** and paste it into the Session Cookies field in Lidarr.

### Troubleshooting

* **Downloads fail with authentication errors** — Your `identity` cookie has expired. Re-export it from your browser and update both the indexer and download client settings.
* **Search returns no results** — Verify your cookies are configured and that you have purchased the release on Bandcamp. Only music in your collection will appear.
* **Plugin doesn't appear** — Ensure Lidarr is on the `nightly` branch and restart after installation.
