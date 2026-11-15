# Authorized media-vm deployment — 2026-11-15

User approved fresh rollback backups, Bandcamp DLL replacement, Lidarr stop/start, and read-only startup/discovery/API checks. No provider edits or explicit Bandcamp tests were authorized or performed. Existing enabled services may resume scheduled work on startup; this is not an account-function qualification.

Fresh protected backup: `/srv/docker-state/rollback-preparation/lidarr-20261115T094057Z`. Both online SQLite backups passed integrity checks; backup hashes, modes and ownership passed readback. All 388 copied host libraries still matched live. The image and old plugin matched preparation before mutation.

Executed once: stage candidate with uid 99/gid 100/mode 0644, compose stop lidarr, independently confirm stopped, atomic DLL rename, compose start lidarr. All steps succeeded. No image recreation or DB/config restore occurred.

Installed and left in place: SHA-256 `4333fbc799674d80a06882d478072ce7c9a67348ee0dba4dffe2da8414dc1fd4`. Container StartedAt `2026-11-15T09:41:48.871027253Z`; status startTime `2026-11-15T09:41:52Z`. Authenticated status returned Lidarr 3.2.1.5196 nightly / .NET 8.0.27, running and not restarting.

Authenticated read-only schema endpoints expose BandcampDownloadClient and BandcampIndexer, both including authConfiguration. Saved downloader 12 remains enabled; saved indexer 26 remains automatic/interactive-search enabled and RSS disabled. Health GET returned one total entry, none sourced to Bandcamp. This is an observation of cached/reportable health, not an explicit CheckHealth invocation or proof of authentication.

Container startup output since restart had no ReflectionTypeLoadException, TypeLoadException, MissingMethodException, unable-to-load or application-startup markers. No warning/error logger events were found in that container output. No Bandcamp-specific loader line was emitted there; successful schema enumeration supplies the discovery evidence. Whole logs/provider fields were not exported.

User coordination reports health compatibility PR #59 merged at e51a1d5a7d01f25c63b73047d4e119357579fce7; its prior temporary test was restored before this deployment. Future #55 integration must retain #59 compatibility adapter/gates. This deployed candidate has its own exact-host constructor correction; it is not claimed to include merged #59.

Remaining: actual host action/persistence workflows, shared-session migration, browser/helper runtime and live account/search/download qualification. #47 Subsonic gate remains inconclusive. No live credentials were emitted. Plugin-only rollback instructions remain in future-install.md; use this fresh backup directory and recheck current state first. No rollback was executed.
