# Approved route-candidate native attempt — 2026-11-15

The user approved deployment of exact-target DLL `ad312e81cbf9bcf3290aa761508fc69a9ee094b519b5c738233f54523c165f08`, restart, and one guarded Dark Matter EP FLAC grab. This scope is spent.

Preflight confirmed all 21 prior grabs had failed terminally, no target commands remained, artist 268 and album 235830 were unmonitored, all four tracks lacked linked files, and the original failed-download retry flag was true. The pinned guarded deployment passed every phase, including host readiness. No inverse ran. Both downloader 12 and shared indexer 26 connection tests passed, and a fresh accepted FLAC search result reported 175531622 bytes.

The full retry config was saved privately, `autoRedownloadFailed` alone was set false, and the complete paused DTO was verified before exactly one POST /release. New download ID `9bada054ef5440adbdbdb0599b7029af` failed at 2026-11-15T15:19:36Z (history 2377416):

> Invalid URI: The format of the URI could not be determined.

The same ID has a terminal failure, no target commands remained, and the full retry config was restored only after comparing the current DTO with the expected paused version. Readback matched the complete prior DTO. There are now 22 grabs and 22 failures, zero linked target files, and no native import qualification. The installed DLL remains ad312e81. No session/provider settings were changed and no rollback was attempted.

The failed resolver body and exception stack are unavailable in the retained logs/history; no root cause is confirmed. Local inspection identifies possible response-pointer parsing/URI normalization gaps, but a synthetic example alone cannot establish which response the native run received. No new resolver/archive/native request was sent after this failure.

The pinned independent client reference already retained in this packet reads `retry_url` and normalizes protocol-relative URLs. This makes resolver response shape a useful next observation, not proof of this failure's cause. A prepared [shape probe](resolver-shape-probe.py) compiles and passes plan-only execution. With separate approval it performs at most two GETs (owned HTML page and correctly converted statdownload route), disables redirects/retries, never follows the returned pointer, reads at most four bytes if an archive header arrives, and records only fixed response-shape categories. It never saves raw URL/body/cookie values. A resolver GET can affect upstream download accounting/conversion. Python transport is not proof of the native response.

Deployment and terminal receipts are alongside this report. Private full search/history/config snapshots remain under `/srv/docker-state/rollback-preparation/lidarr-resolver-route-20261115T150021Z/approved-native-attempt`. Old failure and rollback packets are retained.
