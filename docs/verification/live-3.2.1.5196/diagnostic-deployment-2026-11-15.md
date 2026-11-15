# Authorized login-diagnostic deployment — 2026-11-15

User approved deployment/restart of the typed login diagnosis fix, leaving password login Test to the user. Source integration ae53ac555ad9ae15e37ccfcb222a6b21d5cbc15e. Exact-target build and 304 plugin/30 helper/6 Python checks passed before deployment.

Fresh backup: `/srv/docker-state/rollback-preparation/lidarr-20261115T111647Z`. Online SQLite snapshots passed integrity; receipt hashes/ownership/modes and complete old Bandcamp plugin directory were read back. All 388 deployed host libraries matched prior preparation. Validator candidate output refers to the prior probe DLL, not this deployment; the new DLL was separately hashed after transfer and before replacement.

Old DLL SHA-256: `4333fbc799674d80a06882d478072ce7c9a67348ee0dba4dffe2da8414dc1fd4`.
New installed DLL SHA-256: `9e109bdd7d8b0c8ff417a8e5e4af36b393d1b2c6635ea58ae126f80bfbbd447c`.
Retained candidate: `/srv/docker-state/lidarr-diagnostic-20261115T1116/Lidarr.Plugin.Bandcamp.dll`.

One authorized sequence: validate unchanged image/old DLL/new DLL, stage uid99/gid100 mode0644, compose stop, independently confirm not running, atomic rename, compose start. All steps succeeded. No settings edits, image recreation, restore or explicit login attempt occurred.

Read-only API status confirms 3.2.1.5196/.NET8.0.27, startTime 2026-11-15T11:17:49Z. Container running, StartedAt 2026-11-15T11:17:47.702317021Z. Both BandcampDownloadClient and BandcampIndexer are discoverable through schema APIs. Installed hash read back equals new DLL. Actual prior blank-cookie login cause remains UNKNOWN pending user test with improved classification. Existing enabled scheduled work may resume on startup; no claim of zero autonomous account activity is made.

Plugin-only inverse, if separately authorized: confirm new installed hash and fresh backup old hash, stage the old DLL with original ownership/mode, stop, atomically replace, start, verify. Do not restore DB/config merely to undo this DLL change. Backups and historical evidence retained; no cleanup.
